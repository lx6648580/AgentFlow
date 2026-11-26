using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentFlow.NodeHost;

internal sealed class NativeAgentTurn(
    PlannedNode node, ModelProfiles profiles, ExternalTools tools, RunArchive archive, ExecutionBudget budget,
    Func<RunEventItem, Task> record, string[] destinations, ConversationTurn? conversation) : IChatClient
{
    private readonly ModelProfile profile = node.Profile!;
    private FlowMessage? input;
    private long started;
    private bool failed;
    private ChatOptions? invocation;
    private int reserved;

    public Executor<FlowMessage> CreateInput(string id) => new InputExecutor(id, this);
    public ChatProtocolExecutor CreateOutput(string id) => new OutputExecutor(id, this);

    private bool IsHarness => node.Type == NodeTypes.HarnessAgent;

    public ExecutorBinding CreateAgent(string id)
    {
        int? contextWindow = node.Harness is { ContextWindowTokens: > 0 } config ? config.ContextWindowTokens : null;
        AIAgent agent = node.Harness is { } harness
            ? new HarnessAgent(this, new HarnessAgentOptions
            {
                Id = id, Name = "HarnessAgent", HarnessInstructions = harness.Instructions,
                MaximumIterationsPerRequest = harness.MaximumIterations,
                // 锁定版本中的原生压缩 API 仍为预览，只有节点显式设置上下文窗口时启用。
#pragma warning disable MAAI001
                MaxContextWindowTokens = contextWindow, MaxOutputTokens = contextWindow is null ? null : profile.MaxTokens,
                DisableCompaction = contextWindow is null,
#pragma warning restore MAAI001
                // 外部工具继续由宿主只读清单授权。
                DisableFileMemory = true, DisableAgentSkillsProvider = true, DisableWebSearch = true,
                DisableTodoProvider = true, DisableAgentModeProvider = true, DisableToolAutoApproval = true,
                DisableApprovalResponseBinding = true, DisableApprovalNotRequiredFunctionBypassing = true,
                DisableOpenTelemetry = true
            })
            : new ChatClientAgent(this, new ChatClientAgentOptions { Id = id, Name = "Agent", UseProvidedChatClientAsIs = true });
        return new ConfiguredAgent(agent, this)
            .BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false, ReassignOtherAgentsAsUsers = false });
    }

    private async ValueTask StartAsync(FlowMessage message, IWorkflowContext context, CancellationToken token)
    {
        input = message;
        started = Stopwatch.GetTimestamp();
        budget.Step();
        await Record("NodeStarted", value: message.Value);
        var instructions = FlowValues.Render(node.Text(), message.Value, message.Variables, limit: 16000);
        var references = node.Harness?.Tools ?? ExternalTools.References(node.Parameters["tools"]);
        var bound = await tools.BindAsync(references, (kind, reference, body) => Record(kind, reference, body), token,
            maxCalls: node.Harness?.MaxToolCalls ?? StandardNodes.MaxToolCallsPerTurn);
        reserved = budget.Reserve(profile.MaxTokens);
        invocation = new ChatOptions
        {
            Instructions = instructions, Temperature = (float)profile.Temperature, MaxOutputTokens = reserved, Tools = bound
        };
        await Record("ModelRequest", profile.Id, JsonSerializer.Serialize(new
        {
            attempt = 1,
            executor = "MAF.AIAgentBinding",
            agentType = IsHarness ? "HarnessAgent" : "ChatClientAgent",
            messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = message.Value } },
            options = new { maxOutputTokens = reserved, profile.Temperature, profile.Streaming, tools = references },
            harness = node.Harness is { } harness ? new
            {
                maximumIterations = harness.MaximumIterations, contextWindowTokens = harness.ContextWindowTokens,
                maxToolCalls = harness.MaxToolCalls, resourceNodes = harness.Sources
            } : null
        }));
        await context.SendMessageAsync(new ChatMessage(ChatRole.User, message.Value), token);
        await context.SendMessageAsync(new TurnToken(profile.Streaming), token);
    }

    private async ValueTask CompleteAsync(List<ChatMessage> messages, IWorkflowContext context, CancellationToken token)
    {
        var packet = input ?? throw new GraphError("ExecutionFailed", node.Id);
        var value = AgentCall.Limit(string.Concat(messages.Select(item => item.Text)));
        await Record("NodeCompleted", value: value, duration: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        await context.SendMessageAsync(packet with { Source = node.Id, Port = "text", Value = value }, token);
    }

    public async Task FailAsync(string code)
    {
        if (failed)
            return;
        failed = true;
        await Record("NodeFailed", code, duration: started == 0 ? null : Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var client = OpenTransport(options?.Tools);
        return await client.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = OpenTransport(options?.Tools);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    private IChatClient OpenTransport(IList<AITool>? bound) =>
        IsHarness ? profiles.Open(profile) : AgentCall.OpenClient(profiles, profile, bound);

    private async Task<AgentResponse> InvokeAsync(AIAgent agent, IEnumerable<ChatMessage> messages, AgentSession? session,
        CancellationToken cancellationToken)
    {
        using var timeout = Deadline(cancellationToken);
        var response = await CallProvider(() => agent.RunAsync(messages, session, new ChatClientAgentRunOptions(invocation), timeout.Token),
            cancellationToken, timeout.Token);
        await RecordToolCallsAsync(response.Messages.SelectMany(message => message.Contents));
        if (response.Messages.Any(message => message.Contents.Any(IsToolProtocol)))
        {
            response.Messages = response.Messages.Select(message =>
            {
                var output = message.Clone();
                output.Contents = message.Contents.Where(content => !IsToolProtocol(content)).ToList();
                return output;
            }).ToList();
        }
        AgentCall.Limit(response.Text);
        await RecordUsageAsync(response.Usage);
        await CaptureSessionAsync(agent, session, cancellationToken);
        return response;
    }

    private async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(AIAgent agent, IEnumerable<ChatMessage> messages,
        AgentSession? session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = Deadline(cancellationToken);
        await using var updates = agent.RunStreamingAsync(messages, session, new ChatClientAgentRunOptions(invocation), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        UsageDetails? usage = null;
        var pending = "";
        var length = 0;
        while (await CallProvider(() => updates.MoveNextAsync().AsTask(), cancellationToken, timeout.Token))
        {
            var update = updates.Current;
            length += update.Text.Length;
            if (length > 64000)
                throw new GraphError("OutputLimit", node.Id);
            await RecordDeltaAsync(archive.ScrubDelta(ref pending, update.Text));
            await RecordToolCallsAsync(update.Contents);
            foreach (var content in update.Contents.OfType<UsageContent>())
                (usage ??= new()).Add(content.Details);
            if (update.Contents.Any(IsToolProtocol))
            {
                var portable = update.AsChatResponseUpdate().Clone();
                portable.Contents = update.Contents.Where(content => !IsToolProtocol(content)).ToList();
                update = new AgentResponseUpdate(portable) { AgentId = update.AgentId };
            }
            yield return update;
        }
        await RecordDeltaAsync(archive.ScrubDelta(ref pending, "", final: true));
        await RecordUsageAsync(usage);
        await CaptureSessionAsync(agent, session, cancellationToken);
    }

    private async ValueTask CaptureSessionAsync(AIAgent agent, AgentSession? session, CancellationToken token)
    {
        if (conversation is null)
            return;
        if (session is null)
            throw new GraphError("ConversationSessionUnavailable", node.Id);
        conversation.Sessions[node.Id] = await agent.SerializeSessionAsync(session, cancellationToken: token);
    }

    private ValueTask<AgentSession> CreateSessionAsync(AIAgent agent, CancellationToken token) =>
        conversation is not null && conversation.Sessions.TryGetValue(node.Id, out var session)
            ? agent.DeserializeSessionAsync(session, cancellationToken: token)
            : agent.CreateSessionAsync(token);

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        return timeout;
    }

    private static async Task<T> CallProvider<T>(Func<Task<T>> call, CancellationToken caller, CancellationToken deadline)
    {
        try { return await call(); }
        catch (Exception error) when (AgentCall.ProviderError(error, caller, deadline) is { } classified)
        {
            throw classified;
        }
    }

    private async Task RecordToolCallsAsync(IEnumerable<AIContent> contents)
    {
        foreach (var call in contents.OfType<FunctionCallContent>())
            await Record("ToolCall", call.Name, JsonSerializer.Serialize(call.Arguments));
    }

    // 两种 Agent 各自完成受控工具轮次，进入 Workflow 的回复只包含可转发的结果。
    private static bool IsToolProtocol(AIContent content) =>
        content is FunctionCallContent or FunctionResultContent or ToolApprovalRequestContent or ToolApprovalResponseContent;

    private Task RecordUsageAsync(UsageDetails? usage)
    {
        budget.Count(usage, reserved);
        return Record("ModelResult", profile.Id, usage: usage is null ? null :
            new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount));
    }

    private async Task RecordDeltaAsync(string delta)
    {
        if (delta.Length == 0)
            return;
        if (destinations.Length == 0)
            await Record("IntermediateDelta", delta);
        foreach (var id in destinations)
            await Record("OutputDelta", delta, destination: id);
    }

    private Task Record(string kind, string? text = null, string? value = null, double? duration = null,
        RunUsage? usage = null, string? destination = null) => record(new(0, kind, node.Id, text, DateTimeOffset.UtcNow,
        input?.IterationId, DurationMs: duration, Value: value is null ? null : new(value, value.Length), Usage: usage,
        OutputNodeId: destination));

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private sealed class ConfiguredAgent(AIAgent inner, NativeAgentTurn turn) : DelegatingAIAgent(inner)
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            turn.CreateSessionAsync(InnerAgent, cancellationToken);

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session,
            AgentRunOptions? options, CancellationToken cancellationToken) =>
            turn.InvokeAsync(InnerAgent, messages, session, cancellationToken);

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken) =>
            turn.StreamAsync(InnerAgent, messages, session, cancellationToken);
    }

    [SendsMessage(typeof(ChatMessage))]
    [SendsMessage(typeof(TurnToken))]
    private sealed class InputExecutor(string id, NativeAgentTurn turn) : Executor<FlowMessage>(id)
    {
        public override ValueTask HandleAsync(FlowMessage message, IWorkflowContext context, CancellationToken cancellationToken) =>
            turn.StartAsync(message, context, cancellationToken);
    }

    private sealed class OutputExecutor(string id, NativeAgentTurn turn) : ChatProtocolExecutor(id, new() { AutoSendTurnToken = false })
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) =>
            base.ConfigureProtocol(builder).SendsMessage<FlowMessage>();

        protected override ValueTask TakeTurnAsync(List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents,
            CancellationToken cancellationToken) => turn.CompleteAsync(messages, context, cancellationToken);
    }
}
