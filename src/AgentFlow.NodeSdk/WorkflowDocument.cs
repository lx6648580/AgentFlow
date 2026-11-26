using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFlow;

public sealed record WorkflowDocument(
    string SchemaVersion, string WorkflowId, int Revision,
    List<WorkflowNode> Nodes, List<WorkflowEdge> Edges, Dictionary<string, NodePosition> Layout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowGroup>? Groups = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowLimits? Limits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<WorkflowDocument>? Subworkflows = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static WorkflowDocument Read(string json, NodeCatalog? catalog = null, bool allowIncompleteParameters = false)
    {
        catalog ??= StandardNodes.Catalog;
        var graph = JsonSerializer.Deserialize<WorkflowDocument>(json, Json) ?? throw new GraphError("InvalidDocument");
        Validate(graph, catalog, allowIncompleteParameters, 0, []);
        return graph;
    }

    private static void Validate(WorkflowDocument graph, NodeCatalog catalog, bool allowIncompleteParameters, int depth, HashSet<string> stack)
    {
        if (graph.SchemaVersion != "0.1")
            throw new GraphError("UnsupportedSchema");
        if (string.IsNullOrWhiteSpace(graph.WorkflowId) || graph.Revision < 1 ||
            graph.Nodes is null || graph.Edges is null || graph.Layout is null ||
            graph.Layout.Values.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                p.Width is { } width && (!double.IsFinite(width) || width < 223 || width > 1600) ||
                p.Height is { } height && (!double.IsFinite(height) || height < 130 || height > 1200)))
            throw new GraphError("InvalidDocument");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || node.Config is null)
                throw new GraphError("InvalidDocument");
            if (!ids.Add(node.Id))
                throw new GraphError("DuplicateNode", node.Id);
            var definition = catalog.Get(node.TypeId, node.Version, node.Id);
            if (node.Config.Ports is < 0 or > 16)
                throw new GraphError("InvalidDocument", node.Id);
            if (NodeValidator.Resolve(definition, node.Config).Issues
                .FirstOrDefault(issue => !allowIncompleteParameters || issue.Code != "RequiredParameter") is { } issue)
                throw new GraphError(issue.Code, node.Id, parameterId: issue.Field);
        }
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        if (graph.Nodes.Count(node => node.TypeId == NodeTypes.RunTerminal) > 1)
            throw new GraphError("DuplicateRunTerminal");
        foreach (var edge in graph.Edges)
        {
            if (edge is null || string.IsNullOrWhiteSpace(edge.Id) || !edgeIds.Add(edge.Id))
                throw new GraphError("InvalidDocument");
            graph.ValidateEdge(edge, graph.Edges.Where(other => !ReferenceEquals(edge, other)), catalog);
        }
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in graph.Groups ?? [])
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || !groupIds.Add(group.Id) ||
                group.Nodes is null || group.Nodes.Length == 0 ||
                group.Nodes.Any(id => !ids.Contains(id) || !members.Add(id)))
                throw new GraphError("InvalidGroup");
        }
        if (graph.Layout.Values.Any(p => p.Mode is not (null or "bypass" or "disabled")))
            throw new GraphError("InvalidDocument");
        if (depth > 4 || !stack.Add(graph.WorkflowId) || graph.Subworkflows is { Count: > 32 })
            throw new GraphError("SubworkflowRecursion");
        var revisions = new HashSet<(string, int)>();
        foreach (var child in graph.Subworkflows ?? [])
        {
            if (child is null || !revisions.Add((child.WorkflowId, child.Revision)))
                throw new GraphError("DuplicateSubworkflowVersion");
            Validate(child, catalog, allowIncompleteParameters, depth + 1, stack);
        }
        stack.Remove(graph.WorkflowId);
    }

    public void ValidateEdge(WorkflowEdge edge, IEnumerable<WorkflowEdge> existing, NodeCatalog? catalog = null)
    {
        catalog ??= StandardNodes.Catalog;
        var source = Nodes.Find(node => node.Id == edge.Source) ?? throw new GraphError("UnknownNode", edge.Source);
        var target = Nodes.Find(node => node.Id == edge.Target) ?? throw new GraphError("UnknownNode", edge.Target);
        var from = catalog.Ports(source).FirstOrDefault(p => p.Id == edge.SourcePort && p.Output)
            ?? throw new GraphError("UnknownPort", source.Id, edge.SourcePort);
        var to = catalog.Ports(target).FirstOrDefault(p => p.Id == edge.TargetPort && !p.Output)
            ?? throw new GraphError("UnknownPort", target.Id, edge.TargetPort);
        if (!PortsMatch(from, to))
            throw new GraphError("PortTypeMismatch", target.Id, to.Id);
        if (edge.Kind != EdgeKind(from))
            throw new GraphError("UnsupportedEdge", target.Id, to.Id);
        if (source.Id == target.Id)
            throw new GraphError("UnsupportedEdge", target.Id, to.Id);
        if (existing.Count(e => e.Target == target.Id && e.TargetPort == to.Id) >= (to.MaxConnections ?? 1))
            throw new GraphError("PortOccupied", target.Id, to.Id);
        if (from.MaxConnections is { } maxOutput && existing.Count(e => e.Source == source.Id && e.SourcePort == from.Id) >= maxOutput)
            throw new GraphError("PortOccupied", source.Id, from.Id);
    }

    public static bool PortsMatch(PortDefinition from, PortDefinition to) =>
        from.Type == to.Type && from.ResourceKind == to.ResourceKind;

    public static string EdgeKind(PortDefinition port) =>
        port.Type == "Model" || port.ResourceKind is not null ? "resource" : "message";

    // ponytail: 第一条可执行链只接受三个内置节点。高级拓扑由独立 MAF 实验验证。
    public (WorkflowNode Input, WorkflowNode Normalize, WorkflowNode Output) GetExecutableChain()
    {
        if (Layout.Values.Any(position => position.Mode is not null))
            throw new GraphError("UnsupportedGraph");
        foreach (var node in Nodes)
        {
            foreach (var port in NodeTypes.Ports(node).Where(p => p.Required))
            {
                if (!Edges.Any(e => e.Target == node.Id && e.TargetPort == port.Id))
                    throw new GraphError("MissingInput", node.Id, port.Id);
            }
        }
        if (Nodes.Count(node => node.TypeId != NodeTypes.RunTerminal) != 3 || Edges.Count != 2)
            throw new GraphError("UnsupportedGraph");
        var input = Nodes.Find(n => n.TypeId == NodeTypes.Input);
        var normalize = Nodes.Find(n => n.TypeId == NodeTypes.Normalize);
        var output = Nodes.Find(n => n.TypeId == NodeTypes.Output);
        if (input is null || normalize is null || output is null ||
            !Edges.Any(e => e.Source == input.Id && e.Target == normalize.Id) ||
            !Edges.Any(e => e.Source == normalize.Id && e.Target == output.Id))
            throw new GraphError("UnsupportedGraph");
        return (input, normalize, output);
    }
}

public sealed record WorkflowNode(string Id, string TypeId, int Version, NodeConfig Config);
public sealed record NodeConfig(string? Text = null, int Ports = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, JsonElement>? Parameters = null);
public sealed record WorkflowEdge(string Id, string Source, string SourcePort, string Target, string TargetPort, string Kind);
public sealed record NodePosition(double X, double Y,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Collapsed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Locked = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Width { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Height { get; init; }
}
public sealed record WorkflowGroup(string Id, string? Title, string[] Nodes);

public static class NodeTypes
{
    public const string Input = "core.text-input";
    public const string Normalize = "core.normalize";
    public const string Output = "core.output";
    public const string RunTerminal = "editor.run-terminal";
    public const string Probe = "poc.port-probe";
    public const string Model = "ai.chat-model";
    public const string Agent = "ai.agent";
    public const string HarnessAgent = "ai.harness-agent";
    public const string HarnessInstructions = "harness.instructions";
    public const string HarnessMcpTools = "harness.mcp-tools";
    public const string HarnessFunctionLoop = "harness.function-loop";
    public const string HarnessCompaction = "harness.compaction";
    public const string Prompt = "ai.prompt";
    public const string Condition = "flow.condition";
    public const string Parallel = "flow.parallel";
    public const string Merge = "flow.merge";
    public const string Debug = "core.debug";
    public const string Switch = "flow.switch";
    public const string Loop = "flow.loop";
    public const string ForEach = "flow.for-each";
    public const string Retry = "ai.retry";
    public const string Delay = "flow.delay";
    public const string StructuredOutput = "data.structured-output";
    public const string Variable = "data.variable";
    public const string JsonMapper = "data.json-mapper";
    public const string HttpRead = "tool.http-read";
    public const string FileRead = "tool.file-read";
    public const string SqliteQuery = "tool.sqlite-query";
    public const string McpTool = "tool.mcp";
    public const string JsonTransform = "data.json-transform";
    public const string Subworkflow = "flow.subworkflow";
    public const string Approval = "human.approval";
    public const string UserInput = "human.input";
    public const string SimulatedWrite = "tool.simulated-write";
    public static readonly string[] Standard = [Input, Model, Agent, Prompt, Condition, Parallel, Merge, Debug, Output,
        Switch, Loop, ForEach, Retry, Delay, StructuredOutput, Variable, JsonMapper,
        HttpRead, FileRead, SqliteQuery, McpTool, JsonTransform, Subworkflow, Approval, UserInput, SimulatedWrite, HarnessAgent,
        HarnessInstructions, HarnessMcpTools, HarnessFunctionLoop, HarnessCompaction];
    public static readonly string[] All = [..Standard, Normalize, Probe, RunTerminal];

    public static bool IsHarnessResource(string type) => type is HarnessInstructions or HarnessMcpTools or HarnessFunctionLoop or HarnessCompaction;
    public static bool IsResource(string type) => type == Model || IsHarnessResource(type);

    private static PortDefinition HarnessPort(string id, string kind, bool output)
    {
        var label = kind switch
        {
            HarnessInstructions => new LocalizedText("Harness instructions", "执行指引", "執行指引"),
            HarnessMcpTools => new LocalizedText("MCP tools", "MCP 工具", "MCP 工具"),
            HarnessFunctionLoop => new LocalizedText("Function loop", "函数调用循环", "函式呼叫迴圈"),
            _ => new LocalizedText("Context compaction", "上下文压缩", "上下文壓縮")
        };
        return new(id, "Json", Output: output, Label: label) { ResourceKind = kind };
    }

    internal static IEnumerable<PortDefinition> Ports(WorkflowNode node) => node.TypeId switch
    {
        Input => [new("text", "String", Output: true)],
        Normalize => [new("in", "String", Required: true), new("text", "String", Output: true)],
        Output => [new("value", "String", Required: true)],
        Model => [new("model", "Model", Output: true)],
        Agent or Retry => [new("in", "String", Required: true), new("model", "Model", Required: true), new("text", "String", Output: true)],
        HarnessAgent =>
        [
            new("in", "String", Required: true), new("model", "Model", Required: true), new("text", "String", Output: true),
            HarnessPort("instructions", HarnessInstructions, false), HarnessPort("tools", HarnessMcpTools, false),
            HarnessPort("loop", HarnessFunctionLoop, false), HarnessPort("compaction", HarnessCompaction, false)
        ],
        HarnessInstructions or HarnessMcpTools or HarnessFunctionLoop or HarnessCompaction => [HarnessPort("resource", node.TypeId, true)],
        Prompt => [new("in", "String"), new("text", "String", Output: true)],
        Condition => [new("in", "String", Required: true), new("yes", "String", Output: true), new("no", "String", Output: true)],
        Parallel => [new("in", "String", Required: true), new("a", "String", Output: true), new("b", "String", Output: true), new("c", "String", Output: true)],
        Merge => [new("a", "String"), new("b", "String"), new("c", "String"), new("text", "String", Output: true)],
        Switch => [new("in", "String", Required: true), new("a", "String", Output: true), new("b", "String", Output: true), new("other", "String", Output: true)],
        Loop or ForEach => [new("in", "String", Required: true), new("feedback", "String", Required: true),
            new("body", "String", Output: true, MaxConnections: 1), new("done", "String", Output: true)],
        Debug or Delay or StructuredOutput or Variable or JsonMapper or HttpRead or FileRead or SqliteQuery or McpTool or JsonTransform
            or Subworkflow or Approval or UserInput or SimulatedWrite
            => [new("in", "String", Required: true), new("text", "String", Output: true)],
        Probe => new[] { new PortDefinition("number", "Number") }.Concat(
            Enumerable.Range(1, node.Config.Ports).Select(i => new PortDefinition($"text-{i}", "String"))),
        _ => []
    };
}

public sealed class GraphError(string code, string? nodeId = null, string? portId = null, string? parameterId = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? NodeId { get; } = nodeId;
    public string? PortId { get; } = portId;
    public string? ParameterId { get; } = parameterId;
}
