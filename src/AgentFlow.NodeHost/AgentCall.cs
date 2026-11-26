using System.ClientModel;
using System.Net;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFlow.NodeHost;

internal sealed class ModelCallError(string code, bool retryable) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

internal static class AgentCall
{
    public static async Task<(string Output, UsageDetails? Usage)> ExecuteAsync(ModelProfiles profiles, ModelProfile profile,
        int maxTokens, string instructions, string input, IList<AITool>? tools, Func<string, Task>? record, Func<FunctionCallContent, Task> reportTool,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        var token = timeout.Token;
        using var client = OpenClient(profiles, profile, tools);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "Agent",
            // 只为有显式工具绑定的 Agent 装配函数调用客户端。
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Temperature = (float)profile.Temperature,
                MaxOutputTokens = maxTokens,
                Tools = tools
            }
        });
        try
        {
            if (!profile.Streaming)
            {
                var response = await agent.RunAsync(input, cancellationToken: token);
                foreach (var call in response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
                    await reportTool(call);
                return (Limit(response.Text), response.Usage);
            }

            UsageDetails? usage = null;
            var output = new StringBuilder();
            await foreach (var update in agent.RunStreamingAsync(input, cancellationToken: token))
            {
                if (update.Text.Length > 0)
                {
                    if (output.Length + update.Text.Length > 64000)
                        throw new GraphError("OutputLimit");
                    output.Append(update.Text);
                    if (record is not null)
                        await record(update.Text);
                }
                foreach (var content in update.Contents.OfType<UsageContent>())
                    (usage ??= new()).Add(content.Details);
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                    await reportTool(call);
            }
            return (output.ToString(), usage);
        }
        catch (Exception error) when (ProviderError(error, cancellationToken, timeout.Token) is { } classified)
        {
            throw classified;
        }
    }

    internal static IChatClient OpenClient(ModelProfiles profiles, ModelProfile profile, IList<AITool>? tools) =>
        tools is { Count: > 0 }
            ? new FunctionInvokingChatClient(profiles.Open(profile)) { MaximumIterationsPerRequest = 5, AllowConcurrentInvocation = false }
            : profiles.Open(profile);

    internal static ModelCallError? ProviderError(Exception error, CancellationToken caller, CancellationToken deadline)
    {
        if (error is OperationCanceledException && !caller.IsCancellationRequested && deadline.IsCancellationRequested)
            return new("ModelTimeout", true);
        // 保留固定错误码，供应商返回的正文不进入运行轨迹。
        int? status = error switch
        {
            ClientResultException sdk => sdk.Status,
            HttpRequestException http => (int?)http.StatusCode ?? 0,
            _ => null
        };
        return status is { } value ? new(value > 0 ? "ModelRejected" : "ModelUnavailable",
            value is <= 0 or (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests or >= 500 and <= 599) : null;
    }

    public static string Limit(string output) => output.Length <= 64000 ? output : throw new GraphError("OutputLimit");
}
