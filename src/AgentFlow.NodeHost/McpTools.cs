using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentFlow.NodeHost;

internal sealed partial class ExternalTools
{
    internal static string[] References(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > StandardNodes.MaxAgentToolBindings)
            throw new GraphError("InvalidToolBindings");
        var result = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : "").ToArray();
        foreach (var reference in result)
        {
            var pair = reference.Split('/');
            if (pair.Length != 2 || pair.Any(part => !ToolConnections.Name().IsMatch(part)) || reference.Length > 63)
                throw new GraphError("InvalidToolBindings");
        }
        if (result.Select(reference => reference.Replace("/", "__", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new GraphError("InvalidToolBindings");
        return result;
    }

    public async Task<IList<AITool>?> BindAsync(string[] references, Func<string, string, string?, Task> audit,
        CancellationToken cancellationToken, int maxCalls = StandardNodes.MaxToolCallsPerTurn)
    {
        if (references.Length == 0)
            return null;
        var tools = new List<AITool>();
        var calls = 0;
        foreach (var reference in references)
        {
            var pair = reference.Split('/');
            McpToolInfo description;
            JsonElement schema;
            Uri endpoint;
            try
            {
                var connection = connections.Mcp(pair[0]);
                endpoint = connection.Url;
                if (!connection.Tools.Contains(pair[1]))
                    throw new GraphError("ToolAccessDenied");
                description = (await DiscoverAsync(pair[0], cancellationToken)).SingleOrDefault(tool => tool.Name == pair[1])
                    ?? throw new GraphError("ToolAccessDenied");
                schema = ParameterShape(description.InputSchema);
            }
            catch (GraphError error)
            {
                await audit("ToolRejected", reference, error.Code);
                throw;
            }
            tools.Add(new BoundFunction(reference.Replace("/", "__", StringComparison.Ordinal), description.Description ?? "",
                schema, async (arguments, token) =>
                {
                    await audit("ToolRequest", reference, arguments.GetRawText());
                    try
                    {
                        if (Interlocked.Increment(ref calls) > maxCalls)
                            throw new GraphError("ToolCallLimit");
                        var output = await TimedAsync(10, inner => InvokeMcpAsync(pair[0], pair[1], arguments, inner, endpoint), token);
                        await audit("ToolResult", reference, output);
                        return output;
                    }
                    catch (GraphError error)
                    {
                        await audit("ToolRejected", reference, error.Code);
                        // 返回固定错误码供 Agent 判断后续输出，供应商错误正文不进入日志。
                        return error.Code;
                    }
                }));
        }
        return tools;
    }

    private async Task<string> InvokeMcpAsync(string server, string name, JsonElement input, CancellationToken token, Uri? endpoint = null)
    {
        var allowed = connections.Mcp(server);
        if (!allowed.Tools.Contains(name) || (endpoint is not null && allowed.Url != endpoint))
            throw new GraphError("ToolAccessDenied");
        if (input.ValueKind != JsonValueKind.Object || input.GetRawText().Length > 16000)
            throw new GraphError("InvalidToolArguments");
        await using var client = await OpenMcpAsync(allowed.Url, token);
        var tool = (await DiscoverAsync(client, allowed.Tools, token)).SingleOrDefault(item => item.Name == name)
            ?? throw new GraphError("ToolAccessDenied");
        var schema = FlowValues.Shape.Read(ParameterShape(tool.InputSchema));
        if (!schema.Accepts(input))
            throw new GraphError("InvalidToolArguments");
        var args = input.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value);
        var result = await client.CallToolAsync(name, args, cancellationToken: token);
        if (result.IsError == true)
            throw new GraphError("ToolRemoteError");
        var output = result.StructuredContent is { } data && data.ValueKind != JsonValueKind.Undefined
            ? data.GetRawText()
            : JsonSerializer.Serialize(result.Content.Select(item => item is TextContentBlock text ? text.Text :
                throw new GraphError("ToolContentType")));
        if (Encoding.UTF8.GetByteCount(output) > MaxOutputBytes)
            throw new GraphError("ToolOutputLimit");
        return output;
    }

    private static async Task<McpClient> OpenMcpAsync(Uri endpoint, CancellationToken token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0
        }, McpHttp);
        try { return await McpClient.CreateAsync(transport, cancellationToken: token); }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private static async Task<Tool[]> DiscoverAsync(McpClient client, HashSet<string>? allowed, CancellationToken token)
    {
        var discovered = new Dictionary<string, Tool>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 4; page++)
        {
            var list = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token);
            foreach (var tool in list.Tools.Where(tool => (allowed?.Contains(tool.Name) ?? ToolConnections.Name().IsMatch(tool.Name)) &&
                tool.Annotations is { ReadOnlyHint: true, DestructiveHint: not true }))
            {
                if (!discovered.TryAdd(tool.Name, tool))
                    throw new GraphError("ToolProtocolError");
            }
            cursor = list.NextCursor;
            if (cursor is null)
                return discovered.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        }
        throw new GraphError("ToolDiscoveryLimit");
    }

    // 复用本地 JSON 结构校验。元数据可以公开显示，未实现的验证关键字会被拒绝。
    private static JsonElement ParameterShape(JsonElement schema)
    {
        try
        {
            var compact = JsonSerializer.SerializeToElement(Project(schema, 0));
            if (compact.GetProperty("type").GetString() != "object")
                throw new GraphError("ToolSchemaUnsupported");
            FlowValues.Shape.Read(compact);
            return compact;
        }
        catch (Exception error) when (error is GraphError or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new GraphError("ToolSchemaUnsupported");
        }
    }

    private static Dictionary<string, object?> Project(JsonElement schema, int depth)
    {
        if (depth > 12 || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
            throw new GraphError("ToolSchemaUnsupported");
        var shape = new Dictionary<string, object?>();
        foreach (var field in schema.EnumerateObject())
        {
            switch (field.Name)
            {
                case "type":
                case "required":
                case "additionalProperties":
                    shape.Add(field.Name, field.Value);
                    break;
                case "properties":
                    shape.Add(field.Name, field.Value.EnumerateObject().ToDictionary(property => property.Name,
                        property => Project(property.Value, depth + 1)));
                    break;
                case "items":
                    shape.Add(field.Name, Project(field.Value, depth + 1));
                    break;
                case "description":
                case "title":
                case "$schema":
                case "default":
                case "examples":
                    break;
                default:
                    throw new GraphError("ToolSchemaUnsupported");
            }
        }
        if (type.GetString() == "object" && !shape.ContainsKey("additionalProperties"))
            shape.Add("additionalProperties", false);
        return shape;
    }

    private sealed class BoundFunction(string name, string description, JsonElement schema,
        Func<JsonElement, CancellationToken, Task<string>> invoke) : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            await invoke(JsonSerializer.SerializeToElement(arguments.ToDictionary(item => item.Key, item => item.Value)), cancellationToken);
    }
}
