using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    [Fact]
    public async Task AdvancedSerial_UsesRunVariablesAndProducesOneResult()
    {
        using var api = Client();
        var (run, events) = await ExecuteAdvancedAsync(api, Example("serial-summary"));
        Assert.Equal("Succeeded", run.State);
        Assert.Equal("Mock: Summarize in one sentence: AgentFlow keeps workflow definitions and execution snapshots separate.", run.Output);
        Assert.Equal(new[] { "input", "remember", "prompt", "assistant", "output" }, Started(events));
        await WorkflowTests.EvidenceAsync("phase04-serial.json", new { run, events });
    }

    [Fact]
    public async Task AdvancedParallel_StartsThreeAgentsBeforeTheyFinishThenMergesInPortOrder()
    {
        using var api = Client();
        var (run, events) = await ExecuteAdvancedAsync(api, Example("parallel-review"));
        Assert.Equal("Succeeded", run.State);
        Assert.Equal(ReviewOutput, JsonSerializer.Deserialize<string[]>(run.Output));
        string[] agents = ["reliability", "performance", "maintenance"];
        var starts = events.Where(item => item.Kind == "NodeStarted" && agents.Contains(item.NodeId)).ToArray();
        var completes = events.Where(item => item.Kind == "NodeCompleted" && agents.Contains(item.NodeId)).ToArray();
        Assert.Equal(3, starts.Length);
        Assert.Equal(3, completes.Length);
        Assert.True(starts.Max(item => item.Sequence) < completes.Min(item => item.Sequence));
        Assert.True(completes.Max(item => item.Sequence) < Assert.Single(events, item => item is { Kind: "NodeStarted", NodeId: "merge" }).Sequence);
        Assert.Contains(events, item => item.Kind == "IntermediateDelta");
        Assert.DoesNotContain(events, item => item.Kind == "OutputDelta");
        Assert.NotNull(run.Usage);
        await WorkflowTests.EvidenceAsync("phase04-parallel.json", new { run, events });
    }

    [Fact]
    public async Task Merge_EnforcesAllAndAnyEvenWithConditionalOrSharedSources()
    {
        using var api = Client();
        var score = Example("score-review");
        var mergeIndex = score.Nodes.FindIndex(node => node.Id == "merge");
        score.Nodes[mergeIndex] = score.Nodes[mergeIndex] with { Config = Parameters(new { mode = "all" }) };
        var (missing, _) = await ExecuteAdvancedAsync(api, score);
        Assert.Equal("MissingInput", missing.Error);

        var shared = new WorkflowDocument("0.1", "shared-source-merge", 1,
            [new("input", NodeTypes.Input, 1, new("shared")), new("split", NodeTypes.Parallel, 1, new()),
                new("merge", NodeTypes.Merge, 1, Parameters(new { mode = "any" })), new("output", NodeTypes.Output, 1, new())],
            [new("i", "input", "text", "split", "in", "message"), new("a", "split", "a", "merge", "a", "message"),
                new("b", "split", "b", "merge", "b", "message"), new("o", "merge", "text", "output", "value", "message")], []);
        var (conflict, _) = await ExecuteAdvancedAsync(api, shared);
        Assert.Equal("MergeConflict", conflict.Error);
        shared.Nodes[2] = shared.Nodes[2] with { Config = new("json") };
        var (all, _) = await ExecuteAdvancedAsync(api, shared);
        Assert.Equal(new[] { "shared", "shared" }, JsonSerializer.Deserialize<string[]>(all.Output));
    }

    [Theory]
    [InlineData(82, "approved", "approve", "reject")]
    [InlineData(80, "approved", "approve", "reject")]
    [InlineData(49, "manual-review", "reject", "approve")]
    public async Task ScoreRoute_ValidatesJsonAndRunsOnlySelectedBranch(int score, string expected, string chosen, string skipped)
    {
        using var api = Client();
        var graph = Example("score-review");
        SetText(graph, "input", JsonSerializer.Serialize(new { review = new { score } }));
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Succeeded", run.State);
        Assert.Equal(expected, run.Output);
        Assert.Contains(chosen, Started(events));
        Assert.DoesNotContain(skipped, Started(events));
        await WorkflowTests.EvidenceAsync($"phase04-score-{score}.json", new { run, events });
    }

    [Theory]
    [InlineData(82, "approved")]
    [InlineData(49, "manual-review")]
    [InlineData(63, "fallback")]
    public async Task Switch_RoutesExactCasesAndFallback(int score, string expected)
    {
        using var api = Client();
        var graph = Example("score-review");
        SetText(graph, "input", JsonSerializer.Serialize(new { review = new { score } }));
        var index = graph.Nodes.FindIndex(node => node.Id == "decision");
        graph.Nodes[index] = new("decision", NodeTypes.Switch, 1, Parameters(new { path = "/score", a = 82, b = 49 }));
        graph.Nodes.Add(new("fallback", NodeTypes.Prompt, 1, new("fallback")));
        RenameSourcePort(graph, "decision", "yes", "a");
        RenameSourcePort(graph, "decision", "no", "b");
        graph.Edges.AddRange([new("fallback-in", "decision", "other", "fallback", "in", "message"),
            new("fallback-out", "fallback", "text", "merge", "c", "message")]);
        var (run, _) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Succeeded", run.State);
        Assert.Equal(expected, run.Output);
    }

    [Fact]
    public async Task StructuredOutput_RejectsWrongTypesAndUnsupportedSchemaKeywords()
    {
        using var api = Client();
        var graph = Example("score-review");
        SetText(graph, "input", """{"review":{"score":"82"}}""");
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("InvalidStructuredOutput", run.Error);
        Assert.DoesNotContain("decision", Started(events));
        var schema = """{"type":"object","$ref":"http://example.invalid/schema"}""";
        var index = graph.Nodes.FindIndex(node => node.Id == "structure");
        graph.Nodes[index] = graph.Nodes[index] with { Config = Parameters(new { schema = JsonSerializer.Deserialize<JsonElement>(schema) }) };
        await AssertRejectedAsync(api, graph, "InvalidOutputSchema");
    }

    [Fact]
    public async Task RevisionLoop_StopsAtLimitOrFirstMatchingIteration()
    {
        using var api = Client();
        var graph = Example("revision-loop");
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Mock: Revise: Mock: Revise: Mock: Revise: First draft", run.Output);
        Assert.Equal(new[] { "controller/1", "controller/2", "controller/3" },
            events.Where(item => item is { Kind: "NodeStarted", NodeId: "agent" }).Select(item => item.IterationId));
        Assert.Equal(3, events.Count(item => item.Kind == "IterationCompleted"));
        await WorkflowTests.EvidenceAsync("phase04-loop.json", new { run, events });

        var index = graph.Nodes.FindIndex(node => node.Id == "controller");
        graph.Nodes[index] = graph.Nodes[index] with { Config = Parameters(new { iterations = 3, until = "$ == \"Mock: Revise: First draft\"" }) };
        var (early, earlyEvents) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Mock: Revise: First draft", early.Output);
        Assert.Single(earlyEvents, item => item.Kind == "IterationCompleted");
    }

    [Fact]
    public async Task Batch_PreservesArrayOrderAndTreatsEmptyAndOversizedArraysExplicitly()
    {
        using var api = Client();
        var graph = Example("batch-classification");
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal(new[] { "Mock: Classify: praise", "Mock: Classify: technology", "Mock: Classify: feedback" },
            JsonSerializer.Deserialize<string[]>(run.Output));
        Assert.Equal(3, events.Count(item => item.Kind == "IterationCompleted"));
        await WorkflowTests.EvidenceAsync("phase04-batch.json", new { run, events });
        SetText(graph, "input", "[]");
        var (empty, emptyEvents) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("[]", empty.Output);
        Assert.DoesNotContain("agent", Started(emptyEvents));

        SetText(graph, "input", JsonSerializer.Serialize(Enumerable.Range(1, 9)));
        var (tooLarge, _) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("BatchLimit", tooLarge.Error);
    }

    [Fact]
    public async Task ModelRetry_RecoversFromTransientFailuresAndDoesNotRetryInvalidRequests()
    {
        using var api = Client();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = new ModelProfile("phase04-retry-stub", $"http://127.0.0.1:{port}/v1", "retry-test",
            TimeoutSeconds: 5, Streaming: false);
        await SaveAsync(api, profile);
        var graph = Example("retry-model");
        SetText(graph, "model", profile.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var calls = ServeModelAsync(listener, [503, 429, 200], deadline.Token);
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        await calls;
        Assert.Equal("Succeeded", run.State);
        Assert.Equal("recovered", run.Output);
        Assert.Equal(2, events.Count(item => item.Kind == "RetryScheduled"));
        Assert.Equal(5, run.Usage?.TotalTokens);
        await WorkflowTests.EvidenceAsync("phase04-retry.json", new { run, events, providerCodes = new[] { 503, 429, 200 } });

        var rejectedCall = ServeModelAsync(listener, [400], deadline.Token);
        var (rejected, rejectedEvents) = await ExecuteAdvancedAsync(api, graph);
        await rejectedCall;
        Assert.Equal("ModelRejected", rejected.Error);
        Assert.DoesNotContain(rejectedEvents, item => item.Kind == "RetryScheduled");
    }

    [Fact]
    public async Task WorkflowLimits_StopDelayStepsAndReportedModelUse()
    {
        using var api = Client();
        var delayed = DelayGraph();
        var (timedOut, _) = await ExecuteAdvancedAsync(api, delayed);
        Assert.Equal("TimedOut", timedOut.State);
        Assert.Equal("WorkflowTimeout", timedOut.Error);

        var graph = Example("serial-summary") with { Limits = new(MaxSteps: 2) };
        var (steps, _) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("StepBudgetExceeded", steps.Error);
        var (tokens, _) = await ExecuteAdvancedAsync(api, graph with { Limits = new(MaxModelOutputTokens: 10) });
        Assert.Equal("TokenBudgetExceeded", tokens.Error);
    }

    [Fact]
    public async Task GraphValidation_RejectsUnboundedCyclesMissingInputsAndAgentBypass()
    {
        using var api = Client();
        var graph = Graph("bad cycle");
        graph.Nodes.AddRange([new("x", NodeTypes.Normalize, 1, new()), new("y", NodeTypes.Normalize, 1, new())]);
        graph.Edges.AddRange([new("xy", "x", "text", "y", "in", "message"), new("yx", "y", "text", "x", "in", "message")]);
        await AssertRejectedAsync(api, graph, "InvalidCycle");

        graph = Example("revision-loop");
        graph.Edges.RemoveAll(edge => edge.TargetPort == "feedback");
        await AssertRejectedAsync(api, graph, "MissingInput");
        graph = Example("serial-summary");
        graph.Layout["assistant"] = graph.Layout["assistant"] with { Mode = "bypass" };
        await AssertRejectedAsync(api, graph, "UnsupportedBypass");
        graph = Example("serial-summary");
        graph.Nodes.Add(new("orphan", NodeTypes.Prompt, 1, new("orphan")));
        await AssertRejectedAsync(api, graph, "UnreachableNode");
    }

    [Fact]
    public async Task BypassPreservesInputAndDisabledBranchesDoNotActivateOtherRoutes()
    {
        using var api = Client();
        var graph = DelayGraph();
        graph.Layout["delay"] = new(200, 100, Mode: "bypass");
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("original", run.Output);
        Assert.Contains(events, item => item is { Kind: "NodeBypassed", NodeId: "delay" });

        graph = Example("score-review");
        graph.Layout["reject"] = graph.Layout["reject"] with { Mode = "disabled" };
        var (approved, active) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("approved", approved.Output);
        Assert.DoesNotContain("reject", Started(active));
        SetText(graph, "input", """{"review":{"score":10}}""");
        var (inactive, _) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("NoWorkflowOutput", inactive.Error);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task AdvancedSample_LoadsFromCanvasAndRunsParallelReview()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1740, Height = 960 } });
        await EditorTests.OpenAsync(page);
        await SelectParallelSampleAsync(page);
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        var output = await page.Locator("[data-run-output]").InnerTextAsync();
        Assert.Equal(ReviewOutput, JsonSerializer.Deserialize<string[]>(output));
        if (WorkflowTests.EvidenceDirectory is { } outputDirectory)
            await page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "phase04-parallel-editor.png") });
    }

    [Fact]
    [Trait("Category", "Browser")]
    [Trait("Category", "LiveModel")]
    public async Task AdvancedParallel_ExecutesThreeAgentsWithSelectedLiveModel()
    {
        const string profileId = "qwen-live";
        using var api = Client();
        await SaveAsync(api, new(profileId, RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
            RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
            TimeoutSeconds: 90, Temperature: 0, MaxTokens: 256));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1740, Height = 960 } });
        await EditorTests.OpenAsync(page);
        await SelectParallelSampleAsync(page);
        await page.Locator("[data-node=model] [data-node-model]").SelectOptionAsync(profileId);
        await page.Locator("[data-run-start]").ClickAsync();
        var runId = page.Locator("[data-run-id]");
        await Expect(runId).ToBeVisibleAsync();
        var id = Guid.Parse(await runId.InnerTextAsync());
        var run = await WaitAsync(api, id, Terminal, 100);
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        await WorkflowTests.EvidenceAsync("phase04-live-parallel.json", new { run, events });
        Assert.True(run.State == "Succeeded", $"{run.State} ({run.Error})");
        var results = JsonSerializer.Deserialize<string[]>(run.Output);
        Assert.NotNull(results);
        Assert.Equal(3, results.Length);
        Assert.All(results, item => Assert.False(string.IsNullOrWhiteSpace(item)));
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        if (WorkflowTests.EvidenceDirectory is { } outputDirectory)
            await page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, "phase04-live-parallel-editor.png") });
    }

    private static readonly string[] ReviewOutput =
    [
        "Mock: Reliability: Review the new workflow scheduler.",
        "Mock: Performance: Review the new workflow scheduler.",
        "Mock: Maintenance: Review the new workflow scheduler."
    ];

    private static async Task SelectParallelSampleAsync(IPage page)
    {
        await EditorTests.LoadExampleAsync(page, "parallel-review");
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("12");
        await page.GetByRole(AriaRole.Button, new() { Name = "适应画布", Exact = true }).ClickAsync();
    }

    private static WorkflowDocument Example(string name) =>
        WorkflowDocument.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name + ".workflow.json")));

    private static async Task<(RunSnapshot Run, RunEventItem[] Events)> ExecuteAdvancedAsync(HttpClient api, WorkflowDocument graph)
    {
        var id = await StartAsync(api, new(graph, Guid.NewGuid()));
        return (await WaitAsync(api, id, Terminal), await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events"));
    }

    private static IEnumerable<string?> Started(RunEventItem[] events) =>
        events.Where(item => item.Kind == "NodeStarted").Select(item => item.NodeId);

    private static NodeConfig Parameters<T>(T value) => new(Parameters:
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value)));

    private static void SetText(WorkflowDocument graph, string id, string text)
    {
        var index = graph.Nodes.FindIndex(node => node.Id == id);
        graph.Nodes[index] = graph.Nodes[index] with { Config = graph.Nodes[index].Config with { Text = text } };
    }

    private static void RenameSourcePort(WorkflowDocument graph, string source, string previous, string next)
    {
        var index = graph.Edges.FindIndex(edge => edge.Source == source && edge.SourcePort == previous);
        graph.Edges[index] = graph.Edges[index] with { SourcePort = next };
    }

    private static WorkflowDocument DelayGraph() => new("0.1", "delay-check", 1,
        [new("input", NodeTypes.Input, 1, new("original")), new("delay", NodeTypes.Delay, 1, Parameters(new { milliseconds = 2500 })),
            new("output", NodeTypes.Output, 1, new())],
        [new("delay-in", "input", "text", "delay", "in", "message"), new("delay-out", "delay", "text", "output", "value", "message")], [],
        Limits: new(MaxSeconds: 1));

    private static async Task AssertRejectedAsync(HttpClient api, WorkflowDocument graph, string code)
    {
        using var response = await api.PostAsJsonAsync("api/runs", new RunRequest(graph, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static async Task ServeModelAsync(TcpListener listener, int[] statusCodes, CancellationToken token, string? successfulBody = null)
    {
        foreach (var status in statusCodes)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
            var body = status == 200
                ? successfulBody ?? """{"id":"retry-completion","object":"chat.completion","created":1,"model":"retry-test","choices":[{"index":0,"message":{"role":"assistant","content":"recovered"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":1,"total_tokens":5}}"""
                : """{"error":{"message":"fixture failure","type":"fixture_failure"}}""";
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(bytes, token);
        }
    }
}
