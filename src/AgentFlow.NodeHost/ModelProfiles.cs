using System.Runtime.CompilerServices;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace AgentFlow.NodeHost;

internal sealed class ModelProfiles
{
    private static readonly HttpClient Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, ModelProfile> _profiles;
    private readonly HashSet<Uri> _approvedEndpoints;
    public static string DataDirectory => Path.GetFullPath(
        Environment.GetEnvironmentVariable("AGENTFLOW_RUN_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"), AppContext.BaseDirectory);

    public ModelProfiles()
    {
        _path = Path.Combine(DataDirectory, "model-profiles.json");
        _profiles = File.Exists(_path)
            ? (JsonSerializer.Deserialize<ModelProfile[]>(File.ReadAllText(_path), WorkflowDocument.Json) ?? [])
                .ToDictionary(profile => profile.Id, StringComparer.Ordinal)
            : [];
        _approvedEndpoints = (Environment.GetEnvironmentVariable("AGENTFLOW_MODEL_ENDPOINTS") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEndpoint).ToHashSet();
        _profiles.TryAdd("mock", new("mock", "", "mock"));
    }

    public ModelProfile[] List()
    {
        lock (_gate)
            return _profiles.Values.OrderBy(profile => profile.Id, StringComparer.Ordinal).ToArray();
    }

    public ModelProfile Get(string id)
    {
        lock (_gate)
            return _profiles.GetValueOrDefault(id) ?? throw new GraphError("UnknownModelProfile");
    }

    public void Save(ModelProfile profile, bool createOnly)
    {
        Validate(profile);
        lock (_gate)
        {
            var exists = _profiles.ContainsKey(profile.Id);
            if (createOnly && exists)
                throw new GraphError("ModelProfileExists");
            if (_profiles.Count >= 64 && !exists)
                throw new GraphError("ModelProfileLimit");
            var next = _profiles.Values.Where(item => item.Id != profile.Id).Append(profile).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, WorkflowDocument.Json));
            File.Move(temp, _path, overwrite: true);
            _profiles[profile.Id] = profile;
        }
    }

    public IChatClient Open(ModelProfile profile)
    {
        Validate(profile);
        if (profile.Id == "mock")
            return new MockChatClient();

        var endpoint = ParseEndpoint(profile.BaseUrl);
        var key = profile.SecretRef is null ? null : Environment.GetEnvironmentVariable("AGENTFLOW_MODEL_KEY_" + profile.SecretRef);
        if (profile.SecretRef is not null && string.IsNullOrWhiteSpace(key))
            throw new GraphError("MissingSecret");
        return new OpenAIChatClient(profile.ModelId, new ApiKeyCredential(key ?? "agentflow-local"), new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(Transport),
            RetryPolicy = new ClientRetryPolicy(0)
        }).AsIChatClient();
    }

    public async Task<ToolProbe> ProbeAsync(string id, CancellationToken cancellationToken)
    {
        var profile = Get(id);
        using var client = Open(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        // 仅发送工具声明并检查响应中的调用记录，不执行模型请求的函数。
        var tool = AIFunctionFactory.Create((string value) => value, "agentflow_probe", "Echo a value to verify tool-call support.");
        try
        {
            var response = await client.GetResponseAsync("Call agentflow_probe with the value ready.", new ChatOptions
            {
                MaxOutputTokens = Math.Min(profile.MaxTokens, 128),
                Tools = [tool],
                ToolMode = ChatToolMode.RequireSpecific(tool.Name)
            }, timeout.Token);
            return new(response.Messages.SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>().Any(call => call.Name == tool.Name) ? "Supported" : "NotObserved");
        }
        catch (ClientResultException error) when (error.Status is 400 or 422)
        {
            return new("NotObserved");
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                                      error is OperationCanceledException or ClientResultException or HttpRequestException)
        {
            return new("Unavailable");
        }
    }

    private void Validate(ModelProfile profile)
    {
        if (profile.Id is null || !Regex.IsMatch(profile.Id, @"\A[a-z0-9][a-z0-9_-]{0,39}\z") ||
            string.IsNullOrWhiteSpace(profile.ModelId) || profile.ModelId.Length > 200 ||
            profile.TimeoutSeconds is < 1 or > 600 || profile.MaxTokens is < 1 or > 8192 ||
            !double.IsFinite(profile.Temperature) || profile.Temperature is < 0 or > 2 ||
            (profile.SecretRef is not null && !Regex.IsMatch(profile.SecretRef, @"\A[A-Z0-9_]{1,64}\z")))
            throw new GraphError("InvalidModelProfile");

        if (profile.Id == "mock")
        {
            if (profile.BaseUrl != "" || profile.ModelId != "mock" || profile.SecretRef is not null)
                throw new GraphError("InvalidModelProfile");
            return;
        }

        var endpoint = ParseEndpoint(profile.BaseUrl);
        if (!endpoint.IsLoopback && (!_approvedEndpoints.Contains(endpoint) ||
            (endpoint.Scheme != "https" && !IsPrivateIpv4(endpoint))))
            throw new GraphError("ModelEndpointNotAllowed");
    }

    private static bool IsPrivateIpv4(Uri endpoint)
    {
        if (endpoint.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(endpoint.Host, out var address))
            return false;
        var octets = address.GetAddressBytes();
        return octets[0] == 10 || (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
            (octets[0] == 192 && octets[1] == 168);
    }

    private static Uri ParseEndpoint(string? value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new GraphError("InvalidModelEndpoint");
        return endpoint;
    }
}

internal sealed class MockChatClient : IChatClient
{
    private const string ReplyPrefix = "Mock: ";
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(120, cancellationToken);
        var history = messages.ToArray();
        if (ToolRequest(history, options) is { } tool)
            return new(new ChatMessage(ChatRole.Assistant, [tool]));
        var input = history.Last(message => message.Role == ChatRole.User).Text;
        var output = Reply(history, input);
        return new(new ChatMessage(ChatRole.Assistant, output)) { Usage = Usage(input, output) };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToArray();
        if (ToolRequest(history, options) is { } tool)
        {
            await Task.Delay(120, cancellationToken);
            yield return new(ChatRole.Assistant, [tool]);
            yield break;
        }
        var input = history.Last(message => message.Role == ChatRole.User).Text;
        var output = Reply(history, input);
        for (var offset = 0; offset < output.Length; offset += 24)
        {
            await Task.Delay(120, cancellationToken);
            yield return new(ChatRole.Assistant, output.Substring(offset, Math.Min(24, output.Length - offset)));
        }
        yield return new(ChatRole.Assistant, [new UsageContent(Usage(input, output))]);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private static FunctionCallContent? ToolRequest(ChatMessage[] history, ChatOptions? options)
    {
        if (history.Any(message => message.Role == ChatRole.Tool) || options?.Tools?.OfType<AIFunction>().FirstOrDefault() is not { } function)
            return null;
        if (function.Name == "agentflow_probe")
            return new("mock-tool", function.Name, new Dictionary<string, object?> { ["value"] = "ready" });
        var text = history.Last(message => message.Role == ChatRole.User).Text;
        // 串行 Mock Agent 会把上一节点的原始 JSON 连同固定前缀一起传下去。
        var input = FlowValues.Value(text.StartsWith(ReplyPrefix, StringComparison.Ordinal) ? text[ReplyPrefix.Length..] : text);
        var schema = function.JsonSchema;
        var properties = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var declared)
            ? declared : default;
        var arguments = input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject().Where(field => properties.ValueKind != JsonValueKind.Object || properties.TryGetProperty(field.Name, out _))
                .ToDictionary(field => field.Name, field => (object?)field.Value)
            : [];
        return new("mock-tool", function.Name, arguments);
    }

    private static string Reply(ChatMessage[] history, string input) =>
        history.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault() is { } tool
            ? "Mock tool: " + tool.Result : ReplyPrefix + input;

    // Mock 用固定字符数模拟用量。
    private static UsageDetails Usage(string input, string output) =>
        new() { InputTokenCount = input.Length, OutputTokenCount = output.Length, TotalTokenCount = input.Length + output.Length };
}
