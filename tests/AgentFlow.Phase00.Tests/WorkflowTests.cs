using System.Text.Json;
using AgentFlow.Maf.POC;
using Microsoft.Agents.AI.Workflows;

namespace AgentFlow.Phase00.Tests;

public class WorkflowTests
{
    internal static string SamplePath => Path.Combine(AppContext.BaseDirectory, "minimal.workflow.json");

    [Fact]
    public async Task SharedGraph_ExecutesThreeNodesFromFile()
    {
        var graph = WorkflowDocument.Read(await File.ReadAllTextAsync(SamplePath));
        var trace = new List<RunEvent>();
        var output = await WorkflowRunner.RunAsync(WorkflowRunner.Compile(graph), "", trace.Add);
        Assert.Equal("Workflow", output);
        Assert.Equal(new[] { "input", "normalize", "output" }, Invoked(trace));
        Assert.Equal(output, await WorkflowRunner.RunAsync(WorkflowRunner.Compile(
            graph with { Layout = new() { ["input"] = new(1000, 50) } }), "", _ => { }));
        await EvidenceAsync("maf-serial.json", trace);
    }

    [Theory]
    [InlineData("UnknownPort")]
    [InlineData("PortTypeMismatch")]
    [InlineData("MissingInput")]
    public async Task InvalidGraph_ReturnsStableNodeAndPortDiagnostic(string code)
    {
        var graph = WorkflowDocument.Read(await File.ReadAllTextAsync(SamplePath));
        if (code == "UnknownPort")
            graph.Edges[0] = graph.Edges[0] with { TargetPort = "unknown" };
        else if (code == "PortTypeMismatch")
        {
            graph.Nodes.Add(new("probe", NodeTypes.Probe, 1, new()));
            graph.Edges[0] = graph.Edges[0] with { Target = "probe", TargetPort = "number" };
        }
        else
            graph.Edges.RemoveAt(0);

        var error = Assert.Throws<GraphError>(() =>
            WorkflowRunner.Compile(WorkflowDocument.Read(JsonSerializer.Serialize(graph, WorkflowDocument.Json))));
        Assert.Equal(code, error.Code);
        Assert.Equal(code == "PortTypeMismatch" ? "probe" : "normalize", error.NodeId);
        Assert.Equal(code switch { "UnknownPort" => "unknown", "PortTypeMismatch" => "number", _ => "in" }, error.PortId);
    }

    [Fact]
    public async Task Resource_IsResolvedBeforeExecution()
    {
        var resources = new Dictionary<string, string> { ["prefix"] = "local:" };
        static Workflow Build(string key, Dictionary<string, string> resources)
        {
            if (!resources.TryGetValue(key, out var prefix))
                throw new GraphError("MissingResource", "format", key);
            var format = WorkflowRunner.Bind("format", input => prefix + input);
            return new WorkflowBuilder(format).WithOutputFrom(format).Build();
        }
        Assert.Equal("MissingResource", Assert.Throws<GraphError>(() => Build("missing", resources)).Code);
        var trace = new List<RunEvent>();
        Assert.Equal("local:sample", await WorkflowRunner.RunAsync(Build("prefix", resources), "sample", trace.Add));
        Assert.Equal(new[] { "format" }, Invoked(trace));
        await EvidenceAsync("maf-resource.json", trace);
    }

    [Fact]
    public async Task ConditionalGraph_ExecutesOnlyMatchingBranch()
    {
        var pick = WorkflowRunner.Bind("pick", text => text);
        var yes = WorkflowRunner.Bind("yes", _ => "selected");
        var no = WorkflowRunner.Bind("no", _ => "skipped");
        var result = WorkflowRunner.Bind("result", text => text);
        var workflow = new WorkflowBuilder(pick).AddEdge<string>(pick, yes, text => text == "yes")
            .AddEdge<string>(pick, no, text => text != "yes").AddEdge(yes, result).AddEdge(no, result)
            .WithOutputFrom(result).Build();
        var trace = new List<RunEvent>();
        Assert.Equal("selected", await WorkflowRunner.RunAsync(workflow, "yes", trace.Add));
        Assert.Equal(new[] { "pick", "yes", "result" }, Invoked(trace));
        Assert.Single(trace, e => e.EventType == "WorkflowOutputEvent");
        await EvidenceAsync("maf-condition.json", trace);
    }

    [Fact]
    public async Task Cycle_TerminatesAtExplicitLimit()
    {
        var start = ((Func<int, int>)(count => count)).BindAsExecutor("start");
        var increment = ((Func<int, int>)(count => count + 1)).BindAsExecutor("increment");
        var loop = ((Func<int, int>)(count => count)).BindAsExecutor("loop");
        var finish = ((Func<int, int>)(count => count)).BindAsExecutor("finish");
        var workflow = new WorkflowBuilder(start).AddEdge(start, increment)
            .AddEdge<int>(increment, loop, count => count < 3).AddEdge(loop, increment)
            .AddEdge<int>(increment, finish, count => count >= 3).WithOutputFrom(finish).Build();
        var trace = new List<RunEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(3, await WorkflowRunner.RunAsync(workflow, 0, trace.Add, timeout.Token));
        Assert.Equal(3, Invoked(trace).Count(id => id == "increment"));
        await EvidenceAsync("maf-cycle.json", trace);
    }

    [Fact]
    public async Task FanIn_EmitsOnceAndNextStepWaitsForSlowerBranch()
    {
        var start = WorkflowRunner.Bind("start", text => text);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = WorkflowRunner.Bind("fast", _ => "A");
        var slow = ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
        {
            await releaseSlow.Task.WaitAsync(ct);
            return "B";
        })).BindAsExecutor("slow");
        var downstream = WorkflowRunner.Bind("fast-downstream", text => text);
        ExecutorBinding join = new JoinExecutor();
        var workflow = new WorkflowBuilder(start).AddFanOutEdge(start, [fast, slow])
            .AddEdge(fast, downstream).AddFanInBarrierEdge([fast, slow], join).WithOutputFrom(join).Build();
        var trace = new List<RunEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = await WorkflowRunner.RunAsync(workflow, "go", item =>
        {
            trace.Add(item);
            if (item is { EventType: "ExecutorCompletedEvent", NodeId: "fast" })
                releaseSlow.TrySetResult();
        }, timeout.Token);
        Assert.Equal("AB", output);
        Assert.Single(trace, item => item.EventType == "WorkflowOutputEvent");
        Assert.True(trace.FindIndex(e => e is { EventType: "ExecutorCompletedEvent", NodeId: "slow" }) <
            trace.FindIndex(e => e is { EventType: "ExecutorInvokedEvent", NodeId: "fast-downstream" }));
        await EvidenceAsync("maf-fan-in.json", trace);
    }

    [Fact]
    public async Task ExecutorFailure_ReportsFailedNode()
    {
        var fail = WorkflowRunner.Bind("broken", _ => throw new InvalidOperationException("phase00 failure probe"));
        var trace = new List<RunEvent>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.RunAsync(
            new WorkflowBuilder(fail).WithOutputFrom(fail).Build(), "start", trace.Add));
        Assert.Contains("phase00 failure probe", error.ToString());
        Assert.Contains(trace, e => e is { NodeId: "broken", EventType: "ExecutorFailedEvent" });
        await EvidenceAsync("maf-failure.json", trace);
    }

    [Fact]
    public async Task Checkpoint_RebuiltWorkflowResumesAndChangedIdentityIsRejected()
    {
        static Workflow Build(string firstId = "first")
        {
            var first = WorkflowRunner.Bind(firstId, text => text.Trim());
            var last = WorkflowRunner.Bind("last", text => text + "!");
            return new WorkflowBuilder(first).WithName("phase00-checkpoint").AddEdge(first, last)
                .WithOutputFrom(last).Build();
        }
        var manager = CheckpointManager.CreateInMemory();
        CheckpointInfo checkpoint;
        var initial = new List<RunEvent>();
        await using (var run = await InProcessExecution.RunStreamingAsync(Build(), "  checkpoint  ", manager))
        {
            Assert.Equal("checkpoint!", await WorkflowRunner.ObserveAsync(run, initial.Add));
            Assert.True(run.Checkpoints.Count >= 2);
            checkpoint = run.Checkpoints[0];
        }
        var resumed = new List<RunEvent>();
        await using (var run = await InProcessExecution.ResumeStreamingAsync(Build(), checkpoint, manager))
        {
            Assert.Equal("checkpoint!", await WorkflowRunner.ObserveAsync(run, resumed.Add));
        }
        Assert.Equal(new[] { "last" }, Invoked(resumed));
        var mismatch = await Record.ExceptionAsync(async () =>
        {
            await using var run = await InProcessExecution.ResumeStreamingAsync(Build("changed"), checkpoint, manager);
            await WorkflowRunner.ObserveAsync(run, _ => { });
        });
        await EvidenceAsync("maf-checkpoint.json", new { initial, resumed, incompatible = mismatch?.ToString() });
        Assert.NotNull(mismatch);
    }

    internal static string? EvidenceDirectory => Environment.GetEnvironmentVariable("AGENTFLOW_EVIDENCE");

    internal static async Task EvidenceAsync(string file, object evidence)
    {
        if (EvidenceDirectory is not { } directory)
            return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, file), JsonSerializer.Serialize(evidence, WorkflowDocument.Json));
    }

    private static IEnumerable<string?> Invoked(IEnumerable<RunEvent> trace) =>
        trace.Where(item => item.EventType == "ExecutorInvokedEvent").Select(item => item.NodeId);

    [YieldsOutput(typeof(string))]
    private sealed class JoinExecutor() : Executor<string>("join")
    {
        private readonly List<string> messages = [];

        public override async ValueTask HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken)
        {
            messages.Add(message);
            if (messages.Count == 2)
                await context.YieldOutputAsync(string.Join("", messages.Order()), cancellationToken);
        }
    }
}
