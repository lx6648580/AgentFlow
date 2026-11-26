using System.Text.Json;

namespace AgentFlow.NodeHost;

internal sealed record PlannedPlugin(NodeDefinition Definition, Guid BinaryVersion, Dictionary<string, string> Models);
internal sealed record HarnessConfiguration(string Instructions, string[] Tools, int MaximumIterations, int ContextWindowTokens,
    int MaxToolCalls, Dictionary<string, string> Sources);

internal sealed record PlannedNode(WorkflowNode Node, string? Mode, IReadOnlyDictionary<string, JsonElement> Parameters,
    ModelProfile? Profile = null, Func<string, bool>? Condition = null, string[]? Pointer = null,
    Dictionary<string, string[]>? Mapping = null, FlowValues.Shape? Shape = null, WorkflowPlan? Child = null,
    PlannedPlugin? Plugin = null, HarnessConfiguration? Harness = null)
{
    public string Id => Node.Id;
    public string Type => Node.TypeId;
    public string Text(string field = "text") => Parameters.TryGetValue(field, out var value) ? value.GetString()! : "";
    public int Number(string field) => (int)Parameters[field].GetDecimal();
    public bool Routes => Type is NodeTypes.Condition or NodeTypes.Switch or NodeTypes.Loop or NodeTypes.ForEach;
    public bool RoutesByPort => Routes || Plugin is not null;
    public bool IsController => Type is NodeTypes.Loop or NodeTypes.ForEach;
    public bool IsHuman => Type is NodeTypes.Approval or NodeTypes.UserInput;
    public bool IsAgent => Type is NodeTypes.Agent or NodeTypes.HarnessAgent;
}

internal sealed record WorkflowPlan(WorkflowDocument Definition, WorkflowLimits Limits,
    IReadOnlyDictionary<string, PlannedNode> Nodes, WorkflowEdge[] Edges, string Entry, string[] Outputs,
    ModelProfile[] Profiles)
{
    public bool IsLinear => Nodes.Count == Edges.Length + 1 && Nodes.Values.All(node => !node.Routes &&
        node.Type is not (NodeTypes.Parallel or NodeTypes.Merge));
    public bool Durable => Nodes.Values.Any(node => node.IsHuman);
    public IEnumerable<Guid> PluginVersions => Nodes.Values.SelectMany(node =>
        node.Plugin is { } plugin ? [plugin.BinaryVersion] : node.Child?.PluginVersions ?? Enumerable.Empty<Guid>());

    public (WorkflowPlan Scope, string? BreakAfter) ConfigureDebug(RunDebugOptions? debug)
    {
        if (debug is not null && Nodes.Values.Any(node => node.Type == NodeTypes.HarnessAgent &&
            (!debug.Interactive && (debug.PauseAtStart || debug.BreakpointNode is not null) || debug.StopAfterNode == node.Id)))
            throw new GraphError("HarnessRequiresNativeExecution");
        if (Durable && debug is { Interactive: true })
            throw new GraphError("RunInteractiveUnavailable");
        if (Durable && debug is { } options && (options.PauseAtStart || options.BreakpointNode is not null || options.StopAfterNode is not null))
            throw new GraphError("CheckpointRequiresLinearGraph");
        if (debug is null || (debug.BreakpointNode is null && debug.StopAfterNode is null))
            return (this, null);
        if (!IsLinear)
            throw new GraphError("DebugRequiresLinearGraph");
        if (debug.BreakpointNode is { } breakpoint && !Nodes.ContainsKey(breakpoint))
            throw new GraphError("InvalidDebugNode", breakpoint);
        if (debug.StopAfterNode is { } stop && !Nodes.ContainsKey(stop))
            throw new GraphError("InvalidDebugNode", stop);

        var scope = this;
        if (debug.StopAfterNode is { } end)
        {
            var ids = Traverse(end, Edges.ToLookup(edge => edge.Target), edge => edge.Source);
            scope = this with
            {
                Nodes = Nodes.Where(pair => ids.Contains(pair.Key)).ToDictionary(),
                Edges = Edges.Where(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target)).ToArray(),
                Outputs = [end]
            };
        }
        if (debug.BreakpointNode is { } target && !scope.Nodes.ContainsKey(target))
            throw new GraphError("InvalidDebugNode", target);
        return (scope, scope.Edges.SingleOrDefault(edge => edge.Target == debug.BreakpointNode)?.Source);
    }

    public static WorkflowPlan Validate(WorkflowDocument document, NodeCatalog catalog, NodeRegistry registry, ModelProfiles profiles)
    {
        if (document.Nodes is not { Count: > 0 and <= 128 } || document.Edges is not { Count: <= 384 })
            throw new GraphError("GraphSizeLimit");
        var json = JsonSerializer.Serialize(document, WorkflowDocument.Json);
        if (json.Length > 256000)
            throw new GraphError("InputLimit");
        var graph = WorkflowDocument.Read(json, catalog, allowIncompleteParameters: true);
        return Compile(graph, catalog, registry, profiles.List().ToDictionary(profile => profile.Id), inner: false);
    }

    private static WorkflowPlan Compile(WorkflowDocument graph, NodeCatalog catalog, NodeRegistry registry,
        Dictionary<string, ModelProfile> settings, bool inner)
    {
        if (graph.Nodes.Count is < 1 or > 128 || graph.Edges.Count > 384)
            throw new GraphError("GraphSizeLimit");
        var limits = graph.Limits ?? new();
        if (limits.MaxSteps is < 1 or > 4096 || limits.MaxSeconds is < 1 or > 600 ||
            limits.MaxModelOutputTokens is < 1 or > 100000)
            throw new GraphError("InvalidRunLimits");

        string? Mode(WorkflowNode node) => graph.Layout.GetValueOrDefault(node.Id)?.Mode;
        var active = graph.Nodes.Where(node => node.TypeId != NodeTypes.RunTerminal && Mode(node) != "disabled").ToDictionary(node => node.Id);
        var pluginVersions = new Dictionary<string, Guid>();
        foreach (var node in active.Values)
        {
            if (node.Version != 1 || (!NodeTypes.Standard.Contains(node.TypeId) && node.TypeId != NodeTypes.Normalize))
            {
                if (registry.ExecutorVersion(node.TypeId, node.Version) is not { } version)
                    throw new GraphError("UnsupportedRuntimeNode", node.Id);
                pluginVersions.Add(node.Id, version);
            }
            // 只有无外部权限的单输入转换允许旁路。模型调用和控制节点保留完整的执行语义。
            if (Mode(node) == "bypass" && node.TypeId is not (NodeTypes.Normalize or NodeTypes.Debug or NodeTypes.Delay or NodeTypes.Prompt))
                throw new GraphError("UnsupportedBypass", node.Id);
        }

        var messageNodes = active.Values.Where(node => !NodeTypes.IsResource(node.TypeId)).ToDictionary(node => node.Id);
        var entry = Unique(NodeTypes.Input, "WorkflowStart", messageNodes);
        var outputs = messageNodes.Values.Where(node => node.TypeId == NodeTypes.Output).Select(node => node.Id)
            .Order(StringComparer.Ordinal).ToArray();
        if (outputs.Length == 0)
            throw new GraphError("WorkflowOutput");
        if (inner && outputs.Length != 1)
            throw new GraphError("SubworkflowRequiresSingleOutput");
        var edges = graph.Edges.Where(edge => active.ContainsKey(edge.Source) && active.ContainsKey(edge.Target)).ToArray();
        if (edges.FirstOrDefault(edge => edge.Kind == "resource" && active[edge.Source].TypeId != NodeTypes.Model &&
            (!NodeTypes.IsHarnessResource(active[edge.Source].TypeId) || active[edge.Target].TypeId != NodeTypes.HarnessAgent)) is { } unsupported)
            throw new GraphError("UnsupportedRuntimeResource", unsupported.Source, unsupported.SourcePort);
        var messages = edges.Where(edge => edge.Kind == "message").ToArray();
        var incoming = edges.ToLookup(edge => edge.Target);
        var outgoing = messages.ToLookup(edge => edge.Source);
        var resolvedProfiles = new Dictionary<string, ModelProfile>();
        var children = new Dictionary<(string, int), WorkflowPlan>();

        foreach (var model in active.Values.Where(node => node.TypeId == NodeTypes.Model))
        {
            if (!edges.Any(edge => edge.Source == model.Id && edge.Kind == "resource"))
                throw new GraphError("UnreachableNode", model.Id);
            var id = ReadParameters(model, catalog, bypass: false).GetValueOrDefault("text");
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new GraphError("RequiredParameter", model.Id, parameterId: "text");
            if (!settings.TryGetValue(id.GetString()!.Trim(), out var profile))
                throw new GraphError("UnknownModelProfile", model.Id, parameterId: "text");
            resolvedProfiles[model.Id] = profile;
        }

        var plan = new Dictionary<string, PlannedNode>();
        foreach (var node in messageNodes.Values)
        {
            var mode = Mode(node);
            var config = ReadParameters(node, catalog, mode == "bypass");
            foreach (var port in catalog.Ports(node).Where(port => port.Required && !port.Output))
            {
                if (incoming[node.Id].All(edge => edge.TargetPort != port.Id))
                    throw new GraphError("MissingInput", node.Id, port.Id);
            }
            if (pluginVersions.TryGetValue(node.Id, out var version))
            {
                var incomingPorts = incoming[node.Id].ToLookup(edge => edge.TargetPort);
                if (incomingPorts.FirstOrDefault(port => port.Count() > 1) is { } multiple)
                    throw new GraphError("PluginPortMultiplicity", node.Id, multiple.Key);
                var modelIds = incoming[node.Id].Where(edge => edge.Kind == "resource")
                    .ToDictionary(edge => edge.TargetPort, edge => resolvedProfiles[edge.Source].Id);
                plan[node.Id] = new(node, mode, config,
                    Plugin: new(catalog.Get(node.TypeId, node.Version), version, modelIds));
                continue;
            }
            var modelEdge = incoming[node.Id].SingleOrDefault(edge => edge.Kind == "resource" && edge.TargetPort == "model");
            var nodePlan = new PlannedNode(node, mode, config, modelEdge is null ? null : resolvedProfiles[modelEdge.Source]);
            if (node.TypeId == NodeTypes.HarnessAgent)
            {
                var resources = incoming[node.Id].Where(edge => edge.Kind == "resource")
                    .Select(edge => active[edge.Source]).Where(source => NodeTypes.IsHarnessResource(source.TypeId));
                nodePlan = nodePlan with { Harness = ReadHarness(resources, catalog, nodePlan.Profile!) };
            }
            nodePlan = mode == "bypass" ? nodePlan : ValidateNode(nodePlan, incoming[node.Id].Count());
            if (nodePlan.Type == NodeTypes.Subworkflow)
            {
                var key = (nodePlan.Text("workflow"), nodePlan.Number("revision"));
                if (!children.TryGetValue(key, out var compiled))
                {
                    var definition = graph.Subworkflows?.SingleOrDefault(child => child.WorkflowId == key.Item1 && child.Revision == key.Item2)
                        ?? throw new GraphError("SubworkflowVersionMissing", node.Id);
                    compiled = Compile(definition, catalog, registry, settings, inner: true);
                    children.Add(key, compiled);
                }
                nodePlan = nodePlan with { Child = compiled };
            }
            plan[node.Id] = nodePlan;
        }

        if (plan.Values.Any(node => node.IsController) && plan.Values.FirstOrDefault(node => node.Type == NodeTypes.HarnessAgent) is { } harness)
            throw new GraphError("HarnessRequiresNativeExecution", harness.Id);
        foreach (var controller in plan.Values.Where(node => node.IsController))
            ValidateLoop(controller, plan, incoming, outgoing);
        var forward = messages.Where(edge => edge.TargetPort != "feedback" || !plan[edge.Target].IsController).ToLookup(edge => edge.Source);
        EnsureAcyclic(messageNodes.Keys, forward);
        var reached = Traverse(entry, outgoing, edge => edge.Target);
        var reverse = messages.ToLookup(edge => edge.Target);
        var reachesOutput = outputs.SelectMany(output => Traverse(output, reverse, edge => edge.Source)).ToHashSet();
        foreach (var id in messageNodes.Keys)
        {
            if (!reached.Contains(id))
                throw new GraphError("UnreachableNode", id);
            if (!reachesOutput.Contains(id))
                throw new GraphError("UnreachableOutput", id);
        }

        var compiledPlan = new WorkflowPlan(graph, limits, plan, messages, entry, outputs, resolvedProfiles.Values
            .Concat(children.Values.SelectMany(child => child.Profiles)).Distinct()
            .OrderBy(profile => profile.Id, StringComparer.Ordinal).ToArray());
        if (compiledPlan.Durable && (!compiledPlan.IsLinear || inner))
            throw new GraphError("CheckpointRequiresLinearGraph");
        foreach (var write in plan.Values.Where(node => node.Type == NodeTypes.SimulatedWrite))
        {
            if (inner || !compiledPlan.IsLinear || !plan.Values.Any(node => node.Type == NodeTypes.Approval &&
                    Traverse(node.Id, outgoing, edge => edge.Target).Contains(write.Id)))
                throw new GraphError("ApprovalRequired", write.Id);
        }
        return compiledPlan;
    }

    private static string Unique(string type, string error, Dictionary<string, WorkflowNode> nodes)
    {
        var matches = nodes.Values.Where(node => node.TypeId == type).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : throw new GraphError(error);
    }

    private static HarnessConfiguration ReadHarness(IEnumerable<WorkflowNode> resources, NodeCatalog catalog, ModelProfile model)
    {
        var sources = resources.ToDictionary(node => node.TypeId);
        JsonElement? Read(string kind, string parameter) => sources.TryGetValue(kind, out var source)
            ? ReadParameters(source, catalog, bypass: false).GetValueOrDefault(parameter) : null;
        int Number(string kind, string parameter, int fallback) => Read(kind, parameter) is { } value
            ? (int)value.GetDecimal() : fallback;

        var window = Number(NodeTypes.HarnessCompaction, "contextWindowTokens", 0);
        if (window > 0 && window <= model.MaxTokens)
            throw new GraphError("HarnessContextTooSmall", sources[NodeTypes.HarnessCompaction].Id, parameterId: "contextWindowTokens");

        string[] tools = [];
        try
        {
            if (Read(NodeTypes.HarnessMcpTools, "tools") is { } bindings)
                tools = ExternalTools.References(bindings);
        }
        catch (GraphError error)
        {
            throw new GraphError(error.Code, sources[NodeTypes.HarnessMcpTools].Id, parameterId: error.ParameterId ?? "tools");
        }

        return new(Read(NodeTypes.HarnessInstructions, "text")?.GetString() ?? "", tools,
            Number(NodeTypes.HarnessFunctionLoop, "maximumIterations", StandardNodes.HarnessMaxIterations),
            window, Number(NodeTypes.HarnessFunctionLoop, "maxToolCalls", StandardNodes.MaxToolCallsPerTurn),
            sources.ToDictionary(item => item.Key, item => item.Value.Id));
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadParameters(WorkflowNode node, NodeCatalog catalog, bool bypass)
    {
        var validation = NodeValidator.Resolve(catalog.Get(node.TypeId, node.Version), node.Config);
        if (validation.Issues.FirstOrDefault(issue => !bypass || issue.Code != "RequiredParameter") is { } invalid)
            throw new GraphError(invalid.Code, node.Id, parameterId: invalid.Field);
        if (validation.Values.Values.Any(value => value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 16000))
            throw new GraphError("InputLimit", node.Id);
        return validation.Values;
    }

    private static PlannedNode ValidateNode(PlannedNode node, int inputCount)
    {
        try
        {
            switch (node.Type)
            {
                case NodeTypes.Input when string.IsNullOrWhiteSpace(node.Text()):
                    throw new GraphError("RequiredParameter", parameterId: "text");
                case NodeTypes.Condition:
                    return node with { Condition = FlowValues.Condition(node.Text()) };
                case NodeTypes.Switch:
                    if (JsonElement.DeepEquals(node.Parameters["a"], node.Parameters["b"]))
                        throw new GraphError("DuplicateSwitchCase", parameterId: "b");
                    return node with { Pointer = FlowValues.Pointer(node.Text("path")) };
                case NodeTypes.Loop when node.Text("until").Trim().Length > 0:
                    return node with { Condition = FlowValues.Condition(node.Text("until")) };
                case NodeTypes.Merge when inputCount < (node.Text("mode") == "all" ? 2 : 1):
                    throw new GraphError("MissingInput");
                case NodeTypes.Merge when node.Text() is not ("" or "combine" or "json"):
                    throw new GraphError("InvalidMergeFormat", parameterId: "text");
                case NodeTypes.Variable when node.Text("name") == "input" || !FlowValues.VariableName().IsMatch(node.Text("name")):
                    throw new GraphError("InvalidVariable", parameterId: "name");
                case NodeTypes.StructuredOutput:
                    return node with { Shape = FlowValues.Shape.Read(node.Parameters["schema"]) };
                case NodeTypes.JsonMapper:
                    return node with { Mapping = FlowValues.Mapping(node.Parameters["fields"]) };
                case NodeTypes.JsonTransform:
                    return node with { Pointer = FlowValues.Pointer(node.Text("path")) };
                case NodeTypes.Approval or NodeTypes.UserInput when node.Text("assignee").Length > 80 || node.Text("prompt").Length > 2000:
                    throw new GraphError("InvalidHumanRequest");
                case NodeTypes.Agent:
                    ExternalTools.References(node.Parameters["tools"]);
                    return node;
                case NodeTypes.FileRead when !ToolConnections.IsRelative(node.Text("path")):
                    throw new GraphError("ToolPathDenied", parameterId: "path");
                case NodeTypes.HttpRead when !ToolConnections.Name().IsMatch(node.Text("connection")):
                    throw new GraphError("ToolAccessDenied", parameterId: "connection");
                case NodeTypes.SqliteQuery:
                    if (!ToolConnections.Name().IsMatch(node.Text("source")))
                        throw new GraphError("ToolAccessDenied", parameterId: "source");
                    ExternalTools.Columns(node.Parameters["columns"]);
                    ExternalTools.Filters(node.Parameters["equals"]);
                    return node;
                case NodeTypes.McpTool:
                    if (!ToolConnections.Name().IsMatch(node.Text("server")) || !ToolConnections.Name().IsMatch(node.Text("tool")))
                        throw new GraphError("ToolAccessDenied");
                    if (!node.Parameters["useInput"].GetBoolean() && node.Parameters["arguments"].ValueKind != JsonValueKind.Object)
                        throw new GraphError("InvalidToolArguments", parameterId: "arguments");
                    return node;
                default:
                    return node;
            }
        }
        catch (GraphError error)
        {
            throw new GraphError(error.Code, node.Id, error.PortId, error.ParameterId);
        }
    }

    // ponytail: 循环体使用单入口单出口的链。等子工作流支持隔离后再放开内部并行和嵌套。
    private static void ValidateLoop(PlannedNode controller, Dictionary<string, PlannedNode> nodes,
        ILookup<string, WorkflowEdge> incoming, ILookup<string, WorkflowEdge> outgoing)
    {
        var body = outgoing[controller.Id].SingleOrDefault(edge => edge.SourcePort == "body");
        if (body is null || !outgoing[controller.Id].Any(edge => edge.SourcePort == "done"))
            throw new GraphError("InvalidLoop", controller.Id);
        var visited = new HashSet<string>();
        var edge = body;
        while (edge.Target != controller.Id)
        {
            var node = nodes[edge.Target];
            if (!visited.Add(node.Id) || node.Routes || node.Type is NodeTypes.Input or NodeTypes.Output or NodeTypes.Parallel or NodeTypes.Merge ||
                incoming[node.Id].Count(input => input.Kind == "message") != 1 ||
                outgoing[node.Id].ToArray() is not [var next])
                throw new GraphError("InvalidLoop", controller.Id);
            edge = next;
        }
        if (edge.TargetPort != "feedback")
            throw new GraphError("InvalidLoop", controller.Id, "feedback");
    }

    private static void EnsureAcyclic(IEnumerable<string> ids, ILookup<string, WorkflowEdge> outgoing)
    {
        var degree = ids.ToDictionary(id => id, _ => 0);
        foreach (var edge in outgoing.SelectMany(group => group))
            degree[edge.Target]++;
        var ready = new Queue<string>(degree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        while (ready.TryDequeue(out var source))
        {
            foreach (var edge in outgoing[source])
            {
                if (--degree[edge.Target] == 0)
                    ready.Enqueue(edge.Target);
            }
        }
        if (degree.FirstOrDefault(pair => pair.Value > 0).Key is { } cyclic)
            throw new GraphError("InvalidCycle", cyclic);
    }

    private static HashSet<string> Traverse(string root, ILookup<string, WorkflowEdge> edges, Func<WorkflowEdge, string> next)
    {
        var reached = new HashSet<string> { root };
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var id))
        {
            foreach (var edge in edges[id])
            {
                var target = next(edge);
                if (reached.Add(target))
                    queue.Enqueue(target);
            }
        }
        return reached;
    }
}
