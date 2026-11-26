using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.AI;

namespace AgentFlow.NodeHost;

internal sealed record FlowMessage(string Value, string Source, string Port, IReadOnlyDictionary<string, string> Variables,
    string? IterationId = null);

internal sealed class WorkflowExecution(WorkflowPlan plan, ModelProfiles profiles, ExternalTools tools, RunArchive archive,
    NodeFactory nodeFactory, MemoryCache? cache,
    Func<int?, IEnumerable<string>, CancellationToken, Task>? pause, Func<RunEventItem, Task> record, Guid runId,
    ExecutionBudget budget, HumanCheckpoints? checkpoints = null, string? suppliedInput = null, ConversationTurn? conversation = null,
    bool nativeLockstep = false)
{
    private readonly MemoryCache? _cache = cache;
    private readonly ExecutionBudget _budget = budget;
    private string? SuppliedInput => suppliedInput;
    private bool IsOutput(string nodeId) => plan.Outputs.Contains(nodeId, StringComparer.Ordinal);

    public async Task<ExecutionOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var inputs = plan.Edges.ToLookup(edge => edge.Target);
        var bindings = new Dictionary<string, ExecutorBinding>();
        var requestPorts = new Dictionary<string, RequestPort>();
        var humanNodes = new Dictionary<string, PlannedNode>();
        var agentEntries = new Dictionary<string, ExecutorBinding>();
        var agentBindings = new List<(ExecutorBinding Entry, ExecutorBinding Agent, ExecutorBinding Exit)>();
        var agentExecutors = new Dictionary<string, NativeAgentTurn>();
        var occupied = plan.Nodes.Keys.ToHashSet(StringComparer.Ordinal);
        string InternalId(string id)
        {
            while (!occupied.Add(id))
                id = "$" + id;
            return id;
        }
        // 旧调试请求保留原先的单执行器语义。终端通过原生 Agent 的输出边界推进节点步进。
        var useNativeAgents = (pause is null || nativeLockstep) && !plan.Nodes.Values.Any(node => node.IsController);
        foreach (var node in plan.Nodes.Values)
        {
            if (useNativeAgents && node.IsAgent && !IsOutput(node.Id))
            {
                var turn = new NativeAgentTurn(node, profiles, tools, archive, _budget, record,
                    plan.Edges.Where(edge => edge.Source == node.Id && IsOutput(edge.Target)).Select(edge => edge.Target).ToArray(),
                    conversation);
                ExecutorBinding entry = turn.CreateInput(InternalId("$input:" + node.Id));
                var suffix = 0;
                ExecutorBinding agent;
                do { agent = turn.CreateAgent($"$agent:{node.Id}:{suffix++}"); }
                while (!occupied.Add(agent.Id));
                ExecutorBinding exit = turn.CreateOutput(node.Id);
                agentEntries.Add(node.Id, entry);
                agentBindings.Add((entry, agent, exit));
                foreach (var binding in new[] { entry, agent, exit })
                    agentExecutors.Add(binding.Id, turn);
                bindings.Add(node.Id, exit);
                continue;
            }
            if (!node.IsHuman)
            {
                var ports = inputs[node.Id].ToArray();
                bindings.Add(node.Id, node.Plugin is { } plugin
                    ? new PluginExecutor(node, plugin, ports, this)
                    : new FlowExecutor(node, ports, this));
                continue;
            }
            var key = InternalId("$human:" + node.Id);
            var port = RequestPort.Create<FlowMessage, HumanReply>(key);
            requestPorts.Add(node.Id, port);
            humanNodes.Add(key, node);
            Func<HumanReply, CancellationToken, ValueTask<FlowMessage>> reply = (value, token) => CompleteHumanAsync(node, value, token);
            bindings.Add(node.Id, reply.BindAsExecutor(node.Id));
        }
        ExecutorBinding In(string id) => requestPorts.TryGetValue(id, out var port) ? port : agentEntries.GetValueOrDefault(id, bindings[id]);
        var builder = new WorkflowBuilder(bindings[plan.Entry]);
        foreach (var (entry, agent, reply) in agentBindings)
            builder.AddEdge(entry, agent).AddEdge(agent, reply);
        foreach (var (id, port) in requestPorts)
            builder.AddEdge(port, bindings[id]);
        var barriers = plan.Nodes.Values.Where(node => node.Type == NodeTypes.Merge && node.Text("mode") == "all")
            .Where(node => inputs[node.Id].DistinctBy(edge => edge.Source).Count() == inputs[node.Id].Count() &&
                inputs[node.Id].All(edge => !plan.Nodes[edge.Source].RoutesByPort))
            .Select(node => node.Id).ToHashSet();
        foreach (var merge in barriers)
            builder.AddFanInBarrierEdge(inputs[merge].Select(edge => bindings[edge.Source]), bindings[merge]);
        foreach (var group in plan.Edges.Where(edge => !barriers.Contains(edge.Target)).GroupBy(edge => edge.Source))
        {
            var source = bindings[group.Key];
            if (plan.Nodes[group.Key].RoutesByPort)
            {
                foreach (var target in group.GroupBy(edge => edge.Target))
                {
                    var ports = target.Select(edge => edge.SourcePort).ToHashSet();
                    builder.AddEdge<FlowMessage>(source, In(target.Key), message => message is not null && ports.Contains(message.Port));
                }
            }
            else
            {
                var targets = group.Select(edge => edge.Target).Distinct().Select(In).ToArray();
                if (targets.Length == 1)
                    builder.AddEdge(source, targets[0]);
                else
                    builder.AddFanOutEdge(source, targets);
            }
        }

        var workflow = builder.WithName("AgentFlow-" + plan.Definition.WorkflowId)
            .WithOutputFrom(plan.Outputs.Select(id => bindings[id]).ToArray()).Build();
        var initial = new FlowMessage("", "", "", new Dictionary<string, string>());
        if (pause is not null)
            await pause(null, Array.Empty<string>(), cancellationToken);
        var manager = checkpoints?.Manager;
        var recovery = checkpoints?.Recovery;
        await using var run = recovery is not null
            ? await InProcessExecution.ResumeStreamingAsync(workflow, new(recovery.SessionId, recovery.CheckpointId), manager!,
                cancellationToken: cancellationToken)
            : manager is not null
                ? await InProcessExecution.RunStreamingAsync(workflow, initial, manager, sessionId: checkpoints!.SessionId,
                    cancellationToken: cancellationToken)
                : pause is null
                    ? await InProcessExecution.RunStreamingAsync(workflow, initial, cancellationToken: cancellationToken)
                    : await InProcessExecution.Lockstep.RunStreamingAsync(workflow, initial, cancellationToken: cancellationToken);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        Exception? failure = null;
        while (true)
        {
            ExternalRequest? request = null;
            await foreach (var message in run.WatchStreamAsync(blockOnPendingRequest: manager is null, cancellationToken))
            {
                switch (message)
                {
                    case RequestInfoEvent pending:
                        request = pending.Request;
                        break;
                    case WorkflowOutputEvent { Data: string text, ExecutorId: { } source }:
                        if (!results.TryAdd(source, text))
                        {
                            await Record("NodeFailed", source, "MultipleWorkflowOutputs", null);
                            throw new GraphError("MultipleWorkflowOutputs", source);
                        }
                        break;
                    case ExecutorFailedEvent error:
                        if (!cancellationToken.IsCancellationRequested &&
                            agentExecutors.TryGetValue(error.ExecutorId, out var turn) && error.Data is { } cause and not OperationCanceledException)
                            await turn.FailAsync(ErrorCode(cause));
                        failure ??= error.Data;
                        break;
                    case WorkflowErrorEvent error:
                        failure ??= error.Exception;
                        break;
                    case SuperStepCompletedEvent { CompletionInfo: { } info } batch:
                        var nodes = info.ActivatedExecutors.Order(StringComparer.Ordinal).ToArray();
                        await Record("BatchCompleted", null, null, null, JsonSerializer.Serialize(nodes), batch: batch.StepNumber);
                        if (pause is not null && failure is null && info.HasPendingMessages)
                            await pause(batch.StepNumber, nodes, cancellationToken);
                        break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
                throw new GraphError(ErrorCode(failure));
            if (request is null)
                break;
            if (!request.TryGetDataAs<FlowMessage>(out var packet) || packet is null ||
                !humanNodes.TryGetValue(request.PortInfo.PortId, out var node))
                throw new GraphError("CheckpointIncompatible");
            if (recovery is { Decision: { } decision })
            {
                if (request.RequestId != recovery.Wait.RequestId)
                    throw new GraphError("CheckpointIncompatible");
                await run.SendResponseAsync(request.CreateResponse(new HumanReply(packet, decision.Action, decision.Value)));
                recovery = null;
                continue;
            }

            var checkpoint = run.LastCheckpoint ?? throw new GraphError("CheckpointUnavailable");
            var preview = archive.Scrub(packet.Value);
            var waiting = new HumanWait(request.RequestId, node.Id, node.Type == NodeTypes.Approval ? "approval" : "input",
                node.Text("assignee").Trim(), node.Text("prompt"), preview[..Math.Min(preview.Length, 1024)],
                DateTimeOffset.UtcNow.AddSeconds(node.Number("seconds")));
            return new(null, null, new(waiting, checkpoint));
        }
        if (results.Count == 0)
            throw new GraphError(plan.Nodes.Values.Any(node => node.Type == NodeTypes.Merge && node.Text("mode") == "all")
                ? "MissingInput" : "NoWorkflowOutput");
        var conversationReply = conversation is null ? null : results.GetValueOrDefault(conversation.State.Snapshot.ReplyOutputId);
        if (conversation is not null && string.IsNullOrWhiteSpace(conversationReply))
            throw new GraphError("ConversationReplyMissing");
        var usage = _budget.Snapshot();
        return new(plan.Outputs.Length == 1 ? results.Values.Single() : null, usage.UnknownUsage ? null : usage.Usage,
            ConversationReply: conversationReply);
    }

    private async ValueTask<FlowMessage> CompleteHumanAsync(PlannedNode node, HumanReply reply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        _budget.Step();
        await Record("NodeStarted", node.Id, null, reply.Input.IterationId, reply.Input.Value);
        if (reply.Action == "reject")
        {
            await Record("NodeFailed", node.Id, "ApprovalRejected", reply.Input.IterationId,
                duration: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new GraphError("ApprovalRejected", node.Id);
        }
        var output = node.Type == NodeTypes.UserInput ? reply.Value! : reply.Input.Value;
        await Record("NodeCompleted", node.Id, null, reply.Input.IterationId, output, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return reply.Input with { Source = node.Id, Port = "text", Value = output };
    }

    private async Task<string> ExecuteChildAsync(PlannedNode node, FlowMessage input, CancellationToken token)
    {
        var child = new WorkflowExecution(node.Child!, profiles, tools, archive, nodeFactory, _cache, null, entry => record(entry with
        {
            Kind = entry.Kind == "OutputDelta" ? "IntermediateDelta" : entry.Kind,
            NodeId = entry.NodeId is { } id ? node.Id + "/" + id : null,
            OutputNodeId = null
        }), runId, _budget, suppliedInput: input.Value);
        return (await child.RunAsync(token)).Output!;
    }

    private ValueTask<NodeResult> InvokePluginAsync(PlannedNode node, IReadOnlyDictionary<string, JsonElement> inputs, CancellationToken token) =>
        nodeFactory.ExecuteAsync(runId.ToString("N"), node.Node, inputs, token);

    private bool Matches(WorkflowEdge edge, FlowMessage input) =>
        edge.Source == input.Source && (edge.SourcePort == input.Port || plan.Nodes[input.Source].Type == NodeTypes.Parallel);

    private static void MergeVariables(Dictionary<string, string> values, IReadOnlyDictionary<string, string> incoming, string nodeId)
    {
        foreach (var (name, value) in incoming)
        {
            if (!values.TryAdd(name, value) && values[name] != value)
                throw new GraphError("VariableConflict", nodeId);
        }
        if (values.Count > 32)
            throw new GraphError("VariableLimit", nodeId);
    }

    private async Task<string> WriteSimulationAsync(PlannedNode node, FlowMessage input)
    {
        await Record("ToolRequest", node.Id, "simulated-write", input.IterationId);
        var result = archive.SimulatedWrite(runId, node.Id, input.Value);
        await Record("ToolResult", node.Id, "simulated-write", input.IterationId, result);
        return result;
    }

    private async Task<string> CallAsync(PlannedNode node, FlowMessage input, CancellationToken token)
    {
        var profile = node.Profile!;
        var retry = node.Type == NodeTypes.Retry;
        var attempts = retry ? node.Number("attempts") : 1;
        var instructions = FlowValues.Render(node.Text(), input.Value, input.Variables, limit: 16000);
        var bindings = retry ? [] : ExternalTools.References(node.Parameters["tools"]);
        var boundTools = await tools.BindAsync(bindings,
            (kind, reference, body) => Record(kind, node.Id, reference, input.IterationId, body), token);
        var destinations = retry ? [] : plan.Edges.Where(edge => edge.Source == node.Id && plan.Outputs.Contains(edge.Target))
            .Select(edge => edge.Target).Distinct().ToArray();
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
                _budget.Step();
            var reserve = _budget.Reserve(profile.MaxTokens);
            var pendingDelta = "";
            async Task StreamAsync(string delta, bool final = false)
            {
                var clean = archive.ScrubDelta(ref pendingDelta, delta, final);
                if (clean.Length == 0)
                    return;
                if (destinations.Length == 0)
                    await Record("IntermediateDelta", node.Id, clean, input.IterationId);
                foreach (var destination in destinations)
                    await Record("OutputDelta", node.Id, clean, input.IterationId, outputNodeId: destination);
            }
            try
            {
                await Record("ModelRequest", node.Id, profile.Id, input.IterationId, JsonSerializer.Serialize(new
                {
                    attempt,
                    messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = input.Value } },
                    options = new { maxOutputTokens = reserve, profile.Temperature, profile.Streaming, tools = bindings }
                }));
                var (output, usage) = await AgentCall.ExecuteAsync(profiles, profile, reserve, instructions, input.Value,
                    boundTools, retry ? null : delta => StreamAsync(delta),
                    tool => Record("ToolCall", node.Id, tool.Name, input.IterationId, JsonSerializer.Serialize(tool.Arguments)),
                    token);
                await StreamAsync("", final: true);
                _budget.Count(usage, reserve);
                await Record("ModelResult", node.Id, profile.Id, input.IterationId, usage: usage is null
                    ? null : new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount));
                return output;
            }
            catch (ModelCallError error) when (error.Retryable && attempt < attempts)
            {
                await Record("RetryScheduled", node.Id, error.Code, input.IterationId);
                await Task.Delay(node.Number("milliseconds"), token);
            }
        }
    }

    private async Task<string> UseExternalToolAsync(PlannedNode node, FlowMessage input, CancellationToken token)
    {
        var target = tools.Target(node);
        var arguments = node.Type != NodeTypes.McpTool ? null :
            node.Parameters["useInput"].GetBoolean() ? input.Value : node.Parameters["arguments"].GetRawText();
        await Record("ToolRequest", node.Id, target, input.IterationId, arguments);
        try
        {
            var output = await tools.ExecuteAsync(node, input.Value, token);
            await Record("ToolResult", node.Id, target, input.IterationId, output);
            return output;
        }
        catch (GraphError error)
        {
            await Record("ToolRejected", node.Id, target, input.IterationId, error.Code);
            throw;
        }
    }

    // 显式白名单。缓存只保存确定性文本转换的成功结果，键覆盖类型版本、参数和输入。
    private string? CacheKey(PlannedNode node, FlowMessage message)
    {
        if (_cache is null || node.Mode is not null || node.Type is not (NodeTypes.Normalize or NodeTypes.JsonMapper))
            return null;
        var key = JsonSerializer.Serialize(new { node.Type, node.Node.Version,
            Parameters = node.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal), Input = message.Value });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static string ErrorCode(Exception error) => error switch
    {
        GraphError graph => graph.Code,
        ModelCallError model => model.Code,
        AggregateException { InnerExceptions.Count: 1 } aggregate => ErrorCode(aggregate.InnerExceptions[0]),
        { InnerException: { } inner } => ErrorCode(inner),
        _ => "ExecutionFailed"
    };

    [SendsMessage(typeof(FlowMessage))]
    [YieldsOutput(typeof(string))]
    private sealed class PluginExecutor(PlannedNode node, PlannedPlugin plugin, WorkflowEdge[] incoming, WorkflowExecution execution)
        : Executor<FlowMessage>(node.Id)
    {
        private sealed record PendingInputs(Dictionary<string, JsonElement> Values, Dictionary<string, string> Variables);
        private readonly Dictionary<string, PendingInputs> _pending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PortDefinition> _ports = plugin.Definition.Ports.ToDictionary(port => port.Id);

        public override async ValueTask HandleAsync(FlowMessage message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var iteration = message.IterationId ?? "";
            try
            {
                var matching = incoming.Where(edge => execution.Matches(edge, message)).ToArray();
                if (matching.Length == 0)
                    throw new GraphError("MissingInput", node.Id);
                if (!_pending.TryGetValue(iteration, out var inputs))
                {
                    inputs = new(plugin.Models.ToDictionary(model => model.Key, model => JsonSerializer.SerializeToElement(model.Value)), []);
                    _pending.Add(iteration, inputs);
                }
                foreach (var edge in matching)
                {
                    var port = _ports[edge.TargetPort];
                    var value = port.Type == "String" ? JsonSerializer.SerializeToElement(message.Value) : FlowValues.Json(message.Value);
                    if (!inputs.Values.TryAdd(port.Id, value))
                        throw new GraphError("PluginPortMultiplicity", node.Id, port.Id);
                }
                MergeVariables(inputs.Variables, message.Variables, node.Id);
                if (inputs.Values.Count != incoming.Length + plugin.Models.Count)
                    return;

                _pending.Remove(iteration);
                execution._budget.Step();
                await execution.Record("NodeStarted", node.Id, null, message.IterationId, JsonSerializer.Serialize(inputs.Values));
                var result = await execution.InvokePluginAsync(node, inputs.Values, cancellationToken);
                var outputs = result.Outputs.ToDictionary(pair => pair.Key, pair => AgentCall.Limit(
                    _ports[pair.Key].Type == "String" ? pair.Value.GetString()! : pair.Value.GetRawText()));
                var summary = outputs.Count == 1 ? outputs.Values.Single() : AgentCall.Limit(JsonSerializer.Serialize(result.Outputs));
                await execution.Record("NodeCompleted", node.Id, null, message.IterationId, summary,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (execution.IsOutput(node.Id))
                    await context.YieldOutputAsync(summary, cancellationToken);
                else
                    foreach (var (port, value) in outputs)
                        await context.SendMessageAsync(message with
                        {
                            Source = node.Id, Port = port, Value = value, Variables = inputs.Variables
                        }, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await execution.Record("NodeFailed", node.Id, ErrorCode(error), message.IterationId,
                    duration: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
        }
    }

    [SendsMessage(typeof(FlowMessage))]
    [YieldsOutput(typeof(string))]
    private sealed class FlowExecutor(PlannedNode node, WorkflowEdge[] inputs, WorkflowExecution execution)
        : Executor<FlowMessage>(node.Id)
    {
        private readonly Dictionary<string, FlowMessage> _joined = [];
        private readonly List<string> _batchResults = [];
        private FlowMessage? _original;
        private string[]? _batchItems;
        private int _iteration;
        private int _batchSize = 2;
        private bool _merged;

        public override async ValueTask HandleAsync(FlowMessage message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                execution._budget.Step();
                if (node.Type == NodeTypes.Merge)
                {
                    if (Merge(message) is not { } merged)
                        return;
                    message = merged;
                }
                await execution.Record("NodeStarted", node.Id, null, message.IterationId,
                    node.Type == NodeTypes.Input ? execution.SuppliedInput ?? node.Text() : message.Value);
                cancellationToken.ThrowIfCancellationRequested();
                var key = execution.CacheKey(node, message);
                string? cached = null;
                var hit = key is not null && execution._cache!.TryGetValue(key, out cached);
                var output = hit ? message with { Value = cached!, Port = "text" } : await RunNodeAsync(message, cancellationToken);
                output = output with { Value = AgentCall.Limit(output.Value), Source = node.Id };
                if (key is not null && !hit)
                    execution._cache!.Set(key, output.Value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20) });
                await execution.Record(hit ? "NodeCached" : node.Mode == "bypass" ? "NodeBypassed" : "NodeCompleted",
                    node.Id, null, message.IterationId, output.Value, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (execution.IsOutput(node.Id))
                    await context.YieldOutputAsync(output.Value, cancellationToken);
                else
                    await context.SendMessageAsync(output, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await execution.Record("NodeFailed", node.Id, ErrorCode(error), message.IterationId,
                    duration: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
        }

        private async Task<FlowMessage> RunNodeAsync(FlowMessage input, CancellationToken token)
        {
            if (node.Mode == "bypass")
                return input with { Port = "text" };
            return node.Type switch
            {
                NodeTypes.Input => input with { Value = execution.SuppliedInput ?? node.Text(), Port = "text" },
                NodeTypes.Agent or NodeTypes.Retry => input with { Value = await execution.CallAsync(node, input, token), Port = "text" },
                NodeTypes.Normalize => input with { Value = input.Value.Trim(), Port = "text" },
                NodeTypes.Prompt => input with { Value = Prompt(input), Port = "text" },
                NodeTypes.Condition => input with { Port = node.Condition!(input.Value) ? "yes" : "no" },
                NodeTypes.Switch => input with { Port = Route(input) },
                NodeTypes.Parallel => input with { Port = "*" },
                NodeTypes.Merge or NodeTypes.Debug or NodeTypes.Output => input with { Port = "text" },
                NodeTypes.Loop or NodeTypes.ForEach => await IterateAsync(input),
                NodeTypes.Delay => await DelayAsync(input, token),
                NodeTypes.Variable => SetVariable(input),
                NodeTypes.StructuredOutput => input with { Value = Structure(input.Value), Port = "text" },
                NodeTypes.JsonMapper => input with { Value = Map(input.Value), Port = "text" },
                NodeTypes.JsonTransform => input with { Value = Transform(input.Value), Port = "text" },
                NodeTypes.Subworkflow => input with { Value = await execution.ExecuteChildAsync(node, input, token), Port = "text" },
                NodeTypes.SimulatedWrite => input with { Value = await execution.WriteSimulationAsync(node, input), Port = "text" },
                NodeTypes.HttpRead or NodeTypes.FileRead or NodeTypes.SqliteQuery or NodeTypes.McpTool =>
                    input with { Value = await execution.UseExternalToolAsync(node, input, token), Port = "text" },
                _ => throw new GraphError("UnsupportedRuntimeNode", node.Id)
            };
        }

        private FlowMessage? Merge(FlowMessage input)
        {
            if (_merged)
                throw new GraphError("MergeConflict", node.Id);
            var matched = inputs.Where(edge => execution.Matches(edge, input)).ToArray();
            if (matched.Length == 0)
                throw new GraphError("MissingInput", node.Id);
            if (node.Text("mode") == "any")
            {
                if (matched.Length != 1)
                    throw new GraphError("MergeConflict", node.Id);
                _merged = true;
                return input;
            }
            foreach (var edge in matched)
            {
                if (!_joined.TryAdd(edge.TargetPort, input))
                    throw new GraphError("MergeConflict", node.Id, edge.TargetPort);
            }
            if (_joined.Count != inputs.Length)
                return null;
            _merged = true;
            var ordered = _joined.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).ToArray();
            var variables = new Dictionary<string, string>();
            foreach (var item in ordered)
                MergeVariables(variables, item.Variables, node.Id);
            var values = ordered.Select(item => item.Value);
            var output = node.Text() == "json" ? JsonSerializer.Serialize(values) : string.Join("\n", values);
            return input with { Value = output, Variables = variables };
        }

        private async Task<FlowMessage> IterateAsync(FlowMessage input)
        {
            var target = inputs.Single(edge => execution.Matches(edge, input)).TargetPort;
            var batch = node.Type == NodeTypes.ForEach;
            if (target == "in")
            {
                if (_original is not null)
                    throw new GraphError("InvalidLoop", node.Id);
                _original = input;
                if (batch)
                {
                    var value = FlowValues.Json(input.Value);
                    if (value.ValueKind != JsonValueKind.Array)
                        throw new GraphError("ExpectedArray", node.Id);
                    if (value.GetArrayLength() > node.Number("items"))
                        throw new GraphError("BatchLimit", node.Id);
                    if (value.GetArrayLength() == 0)
                        return input with { Value = "[]", Port = "done" };
                    _batchItems = value.EnumerateArray().Select(item =>
                        item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText()).ToArray();
                }
            }
            else
            {
                if (_original is null || input.IterationId != IterationId())
                    throw new GraphError("InvalidLoop", node.Id);
                await execution.Record("IterationCompleted", node.Id, null, input.IterationId);
                if (batch)
                {
                    var element = JsonSerializer.Serialize(input.Value);
                    _batchSize += element.Length + (_batchResults.Count == 0 ? 0 : 1);
                    if (_batchSize > 64000)
                        throw new GraphError("OutputLimit", node.Id);
                    _batchResults.Add(element);
                    if (_iteration == _batchItems!.Length)
                        return _original with { Value = "[" + string.Join(',', _batchResults) + "]", Port = "done" };
                }
                else if (_iteration == node.Number("iterations") || node.Condition?.Invoke(input.Value) == true)
                    return input with { Port = "done", IterationId = _original.IterationId };
            }

            _iteration++;
            var iteration = IterationId();
            await execution.Record("IterationStarted", node.Id, null, iteration);
            return batch
                ? _original with { Value = _batchItems![_iteration - 1], Port = "body", IterationId = iteration }
                : input with { Port = "body", IterationId = iteration };
        }

        private string IterationId() => $"{node.Id}/{_iteration}";

        private string Prompt(FlowMessage input) => node.Text().Length == 0
            ? input.Value : FlowValues.Render(node.Text(), input.Value, input.Variables);

        private string Route(FlowMessage input)
        {
            var value = FlowValues.At(FlowValues.Value(input.Value), node.Pointer!);
            if (JsonElement.DeepEquals(value, node.Parameters["a"]))
                return "a";
            return JsonElement.DeepEquals(value, node.Parameters["b"]) ? "b" : "other";
        }

        private async Task<FlowMessage> DelayAsync(FlowMessage input, CancellationToken token)
        {
            await Task.Delay(node.Number("milliseconds"), token);
            return input with { Port = "text" };
        }

        private FlowMessage SetVariable(FlowMessage input)
        {
            if (input.Value.Length > 16000)
                throw new GraphError("VariableLimit", node.Id);
            var variables = new Dictionary<string, string>(input.Variables) { [node.Text("name")] = input.Value };
            if (variables.Count > 32)
                throw new GraphError("VariableLimit", node.Id);
            return input with { Variables = variables, Port = "text" };
        }

        private string Structure(string input)
        {
            var value = FlowValues.Json(input, "InvalidStructuredOutput");
            if (!node.Shape!.Accepts(value))
                throw new GraphError("InvalidStructuredOutput", node.Id);
            return value.GetRawText();
        }

        private string Map(string input)
        {
            var value = FlowValues.Json(input);
            return JsonSerializer.Serialize(node.Mapping!.ToDictionary(field => field.Key, field => FlowValues.At(value, field.Value)));
        }

        private string Transform(string input)
        {
            var value = FlowValues.At(FlowValues.Json(input), node.Pointer!);
            return node.Text("format") == "text" && value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        }
    }

    private Task Record(string kind, string? node, string? text, string? iteration, string? value = null, double? duration = null,
        int? batch = null, RunUsage? usage = null, string? outputNodeId = null) => record(new(0, kind, node, text, DateTimeOffset.UtcNow,
        iteration, batch, duration, value is null ? null : new(value, value.Length), usage, outputNodeId));
}
