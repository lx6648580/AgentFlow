using System.Text.Json;
using AgentFlow.SampleNodes;

namespace AgentFlow.Phase00.Tests;

public class NodeSdkTests
{
    [Fact]
    public void CatalogDocumentation_CoversAllBuiltInNodesAndRoundTripsPluginHelp()
    {
        var definitions = StandardNodes.Catalog.Definitions.ToArray();
        Assert.Equal(NodeTypes.All.Length, definitions.Length);
        foreach (var definition in definitions.Concat(Samples().CreateCatalog().Definitions))
        {
            var help = Assert.IsType<NodeDocumentation>(definition.Documentation);
            Assert.All(new[] { help.Usage.En, help.Usage.Hans, help.Usage.Hant,
                help.MafMapping.En, help.MafMapping.Hans, help.MafMapping.Hant },
                value => Assert.False(string.IsNullOrWhiteSpace(value)));
            var json = JsonSerializer.Serialize(definition, WorkflowDocument.Json);
            Assert.Equal(help, JsonSerializer.Deserialize<NodeDefinition>(json, WorkflowDocument.Json)?.Documentation);
        }
    }

    internal static NodeRegistry Samples()
    {
        var registry = new NodeRegistry();
        StandardNodes.Register(registry);
        new SamplePlugin().Register(registry);
        return registry;
    }

    [Fact]
    public void Registry_RejectsDuplicateVersionAndResolvesExactVersion()
    {
        var registry = Samples();
        var definition = registry.CreateCatalog().Get("sample.concat", 1);
        Assert.Equal("DuplicateNodeVersion", Assert.Throws<GraphError>(() => registry.Register(definition)).Code);
        registry.Register(definition with { Version = 2 });
        var catalog = registry.CreateCatalog();
        Assert.Equal(1, catalog.Get("sample.concat", 1).Version);
        Assert.Equal(2, catalog.GetLatest("sample.concat").Version);
        Assert.Equal("UnknownNodeVersion", Assert.Throws<GraphError>(() => catalog.Get("sample.concat", 3)).Code);
        Assert.Equal("UnknownNodeType", Assert.Throws<GraphError>(() => catalog.Get("sample.unknown", 1)).Code);
        Assert.Equal("English", new LocalizedText("English").Get("zh-Hant", "fallback"));
        Assert.Equal("fallback", new LocalizedText().Get("zh-Hans", "fallback"));
    }

    [Theory]
    [InlineData("repeat", "0", "ParameterRange")]
    [InlineData("repeat", "2.5", "ParameterRange")]
    [InlineData("repeat", "\"2\"", "ParameterType")]
    [InlineData("casing", "\"invalid\"", "ParameterChoice")]
    [InlineData("extra", "null", "UnknownParameter")]
    public void Parameters_RejectInvalidInput(string id, string value, string code)
    {
        var definition = Samples().CreateCatalog().Get("sample.concat", 1);
        var config = new NodeConfig(Parameters: new() { [id] = Parse(value) });
        var issue = Assert.Single(new NodeValidator().Validate(definition, config).Issues);
        Assert.Equal(new NodeIssue(code, id), issue);
    }

    [Fact]
    public void RequiredParameter_AllowsIncompleteDraftAndRejectsExportedFile()
    {
        var definition = new NodeDefinition("test.required", 1, null, [], [new("key", "text", Required: true)]);
        var catalog = new NodeCatalog([definition]);
        var graph = new WorkflowDocument("0.1", "test", 1, [new("n", definition.TypeId, 1, new())], [], []);
        var json = JsonSerializer.Serialize(graph, WorkflowDocument.Json);
        var error = Assert.Throws<GraphError>(() => WorkflowDocument.Read(json, catalog));
        Assert.Equal("RequiredParameter", error.Code);
        Assert.Equal("n", error.NodeId);
        Assert.Equal("key", error.ParameterId);
        Assert.Single(WorkflowDocument.Read(json, catalog, allowIncompleteParameters: true).Nodes);
    }

    [Fact]
    public void CatalogJson_DistinguishesExplicitNullDefaultFromMissingDefault()
    {
        var text = JsonSerializer.Serialize(Samples().CreateCatalog().Definitions, WorkflowDocument.Json);
        var catalog = new NodeCatalog(JsonSerializer.Deserialize<NodeDefinition[]>(text, WorkflowDocument.Json)!);
        var picker = catalog.Get("sample.json-pick", 1);
        var configured = NodeValidator.Resolve(picker, new());
        Assert.Equal(JsonValueKind.Null, configured.Values["fallback"].ValueKind);
        var standard = catalog.Get(NodeTypes.Input, 1);
        Assert.Empty(NodeValidator.Resolve(standard, new()).Values);
    }

    [Fact]
    public async Task Samples_ExecuteWithDefaultsAndConfiguredValues()
    {
        var factory = new NodeFactory(Samples());
        var input = new Dictionary<string, JsonElement> { ["left"] = Value("a"), ["right"] = Value("b") };
        var concat = new WorkflowNode("concat", "sample.concat", 1, new());
        var result = await factory.ExecuteAsync("first", concat, input);
        Assert.Equal("a b", result.Outputs["text"].GetString());
        var config = new NodeConfig(Parameters: new() { ["repeat"] = Value(2), ["separator"] = Value("-"), ["casing"] = Value("upper") });
        Assert.Equal("A-BA-B", (await factory.ExecuteAsync("second", concat with { Config = config }, input)).Outputs["text"].GetString());
        var picker = new WorkflowNode("pick", "sample.json-pick", 1,
            new(Parameters: new() { ["caseSensitive"] = Value(false), ["fallback"] = Value(99) }));
        var json = new Dictionary<string, JsonElement> { ["json"] = Parse("{\"Value\":17}") };
        Assert.Equal(17, (await factory.ExecuteAsync("third", picker, json)).Outputs["out"].GetInt32());
        json["json"] = Parse("{}");
        Assert.Equal(99, (await factory.ExecuteAsync("fourth", picker, json)).Outputs["out"].GetInt32());
    }

    [Fact]
    public async Task Factory_ValidatesInputsAndHonorsCancellation()
    {
        var factory = new NodeFactory(Samples());
        var node = new WorkflowNode("concat", "sample.concat", 1, new());
        var missing = await Assert.ThrowsAsync<GraphError>(async () =>
            await factory.ExecuteAsync("run", node, new Dictionary<string, JsonElement>()));
        Assert.Equal("MissingPortValue", missing.Code);
        Assert.Equal("left", missing.PortId);
        var invalid = await Assert.ThrowsAsync<GraphError>(async () =>
            await factory.ExecuteAsync("run", node, new Dictionary<string, JsonElement> { ["left"] = Value(5) }));
        Assert.Equal("PortTypeMismatch", invalid.Code);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await factory.ExecuteAsync("run", node, new Dictionary<string, JsonElement>(), new CancellationToken(true)));
    }

    [Fact]
    public async Task Factory_SeparatesNodeStateAcrossConcurrentRuns()
    {
        var registry = new NodeRegistry();
        registry.Register(new("test.state", 1, null, [new("out", "Number", Output: true, Required: true)], []), () => new Stateful());
        var factory = new NodeFactory(registry);
        var node = new WorkflowNode("state", "test.state", 1, new());
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i =>
            factory.ExecuteAsync($"r{i}", node, new Dictionary<string, JsonElement>()).AsTask()));
        Assert.All(results, result => Assert.Equal(1, result.Outputs["out"].GetInt32()));
    }

    [Fact]
    public void HarnessResources_RequireMatchingPortsAndCanBeShared()
    {
        var graph = new WorkflowDocument("0.1", "harness-resources", 1,
            [new("tools", NodeTypes.HarnessMcpTools, 1, new()),
             new("first", NodeTypes.HarnessAgent, 1, new()),
             new("second", NodeTypes.HarnessAgent, 1, new())], [], []);
        var first = new WorkflowEdge("first-tools", "tools", "resource", "first", "tools", "resource");
        var second = first with { Id = "second-tools", Target = "second" };
        graph.ValidateEdge(first, []);
        graph.ValidateEdge(second, [first]);
        Assert.Equal("PortOccupied", Assert.Throws<GraphError>(() => graph.ValidateEdge(first, [first])).Code);
        Assert.Equal("PortTypeMismatch", Assert.Throws<GraphError>(() => graph.ValidateEdge(first with { TargetPort = "compaction" }, [])).Code);
        Assert.Equal("UnsupportedEdge", Assert.Throws<GraphError>(() => graph.ValidateEdge(first with { Kind = "message" }, [])).Code);
        var agent = StandardNodes.Catalog.Get(NodeTypes.HarnessAgent, 1);
        Assert.Equal(new NodeIssue("UnknownParameter", "tools"), Assert.Single(NodeValidator.Resolve(agent,
            new(Parameters: new() { ["tools"] = Value(Array.Empty<string>()) })).Issues));
    }

    [Fact]
    public void Graph_EnforcesBothPortConnectionLimits()
    {
        var catalog = new NodeCatalog([
            new("test.source", 1, null, [new("out", "String", Output: true, MaxConnections: 1)], []),
            new("test.target", 1, null, [new("in", "String", MaxConnections: 2)], [])
        ]);
        var nodes = new[] { "a", "b", "c", "d" }.Select(id =>
            new WorkflowNode(id, id == "d" ? "test.target" : "test.source", 1, new())).ToList();
        var graph = new WorkflowDocument("0.1", "test", 1, nodes,
            [new("e1", "a", "out", "d", "in", "message"), new("e2", "b", "out", "d", "in", "message")], []);
        Assert.Equal(2, WorkflowDocument.Read(JsonSerializer.Serialize(graph, WorkflowDocument.Json), catalog).Edges.Count);
        var overInput = Assert.Throws<GraphError>(() => graph.ValidateEdge(new("e3", "c", "out", "d", "in", "message"), graph.Edges, catalog));
        Assert.Equal(("PortOccupied", "d"), (overInput.Code, overInput.NodeId));
        var overOutput = Assert.Throws<GraphError>(() => graph.ValidateEdge(graph.Edges[0], graph.Edges.Take(1), catalog));
        Assert.Equal(("PortOccupied", "a"), (overOutput.Code, overOutput.NodeId));
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class Stateful : INodeExecutor
    {
        private int count;
        public async ValueTask<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return new(new Dictionary<string, JsonElement> { ["out"] = Value(++count) });
        }
    }
}
