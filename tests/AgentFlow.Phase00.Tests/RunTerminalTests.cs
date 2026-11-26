using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    [Fact]
    [Trait("Category", "RunHost")]
    public async Task RunTerminal_StepsThroughOneNativeHarnessNodeAndKeepsTheSameRun()
    {
        var (options, client) = CreateConversationHost();
        using var api = client;
        using var host = StartConversationHost(options);
        try
        {
            await ReadyAsync(api);
            var graph = Graph("terminal input");
            UseHarness(graph);
            graph.Nodes.Add(new("terminal", NodeTypes.RunTerminal, 1, new()));
            var id = await StartAsync(api, new(graph, Guid.NewGuid(), new() { Interactive = true, StepAtStart = true }));
            var first = await WaitAsync(api, id, run => run.State == "Paused");
            Assert.True(first.CanStepNode);
            var path = $"api/runs/{id}/events";
            Assert.Equal(new[] { "input" }, Started(await GetAsync<RunEventItem[]>(api, path)));
            await SendControlAsync(api, first, "stepNode");
            var second = await WaitAsync(api, id, run => run.State == "Paused" && run.Sequence > first.Sequence);
            var events = await GetAsync<RunEventItem[]>(api, path);
            Assert.Equal(new[] { "input", "agent" }, Started(events));
            Assert.Single(events, item => item is { NodeId: "agent", Kind: "NodeCompleted" });
            using var request = JsonDocument.Parse(Assert.Single(events, item => item.Kind == "ModelRequest").Value!.Preview);
            Assert.Equal("MAF.AIAgentBinding", request.RootElement.GetProperty("executor").GetString());
            await SendControlAsync(api, second, "continue");
            var final = await WaitAsync(api, id, Terminal);
            Assert.Equal("Succeeded", final.State);
            Assert.Equal(id, final.RunId);
            Assert.DoesNotContain(await GetAsync<RunEventItem[]>(api, path), item => item.NodeId == "terminal");
        }
        finally { StopHost(host); }
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task RunTerminal_RequestsPauseUntilSafeBoundaryThenStopsTheSameRun()
    {
        using var api = Client();
        var graph = TerminalGraph("pause probe");
        var delay = graph.Nodes.FindIndex(node => node.Id == "trim");
        graph.Nodes[delay] = new("trim", NodeTypes.Delay, 1, new(Parameters: new()
        {
            ["milliseconds"] = JsonSerializer.SerializeToElement(1000)
        }));
        var id = await StartAsync(api, new(graph, Guid.NewGuid(), new() { Interactive = true }));
        try
        {
            var latest = await WaitAsync(api, id, run => run.CanPause);
            await SendControlAsync(api, latest, "pause");
            var paused = await WaitAsync(api, id, run => run.State == "Paused");
            Assert.False(paused.PauseRequested);
            var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
            Assert.Contains(events, item => item.Kind == "PauseRequested");
            Assert.DoesNotContain(events, item => item is { NodeId: "output", Kind: "NodeStarted" });
            using var stop = await api.PostAsync($"api/runs/{id}/cancel", null);
            stop.EnsureSuccessStatusCode();
            Assert.Equal("Cancelled", (await WaitAsync(api, id, Terminal)).State);
        }
        finally { using var _ = await api.PostAsync($"api/runs/{id}/cancel", null); }
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task RunTerminal_StepsOneMafBatchForAParallelWorkflow()
    {
        using var api = Client();
        var graph = Example("parallel-review");
        var id = await StartAsync(api, new(graph, Guid.NewGuid(), new() { Interactive = true, StepAtStart = true }));
        try
        {
            var first = await WaitAsync(api, id, run => run.State == "Paused");
            Assert.False(first.CanStepNode);
            var path = $"api/runs/{id}/events";
            var before = await GetAsync<RunEventItem[]>(api, path);
            await SendControlAsync(api, first, "stepBatch");
            var second = await WaitAsync(api, id, run => run.State == "Paused" && run.Sequence > first.Sequence);
            var after = await GetAsync<RunEventItem[]>(api, path);
            Assert.Single(after, item => item.Sequence > before[^1].Sequence && item.Kind == "BatchCompleted");
            await SendControlAsync(api, second, "continue");
            Assert.Equal("Succeeded", (await WaitAsync(api, id, Terminal)).State);
        }
        finally { using var _ = await api.PostAsync($"api/runs/{id}/cancel", null); }
    }

    [Fact]
    [Trait("Category", "RunHost")]
    [Trait("Category", "LiveModel")]
    public async Task RunTerminal_RealHarnessAgentReturnsActualResponseAfterStepping()
    {
        const string profileId = "phase07-terminal-qwen";
        using var api = Client();
        await SaveAsync(api, new(profileId, RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
            RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
            TimeoutSeconds: 90, Temperature: 0, MaxTokens: 256));
        const string expected = "TERMINAL-QWEN-07";
        var graph = Graph($"Reply with exactly this token and no explanation: {expected}", profileId);
        UseHarness(graph);
        graph.Nodes.Add(new("terminal", NodeTypes.RunTerminal, 1, new()));
        var id = await StartAsync(api, new(graph, Guid.NewGuid(), new() { Interactive = true, StepAtStart = true }));
        try
        {
            var first = await WaitAsync(api, id, run => run.State == "Paused" || Terminal(run));
            Assert.Equal("Paused", first.State);
            await SendControlAsync(api, first, "stepNode");
            var second = await WaitAsync(api, id, run => run.Sequence > first.Sequence && (run.State == "Paused" || Terminal(run)), 95);
            Assert.Equal("Paused", second.State);
            var path = $"api/runs/{id}/events";
            var pausedEvents = await GetAsync<RunEventItem[]>(api, path);
            var response = Assert.Single(pausedEvents, item => item is { Kind: "NodeCompleted", NodeId: "agent" });
            Assert.Contains(expected, response.Value!.Preview);
            Assert.DoesNotContain(pausedEvents, item => item is { Kind: "NodeStarted", NodeId: "output" });
            await SendControlAsync(api, second, "continue");
            var run = await WaitAsync(api, id, Terminal);
            Assert.Equal("Succeeded", run.State);
            Assert.Contains(expected, run.Output);
            await WorkflowTests.EvidenceAsync("run-terminal-qwen.json", new { run, events = await GetAsync<RunEventItem[]>(api, path) });
        }
        finally { using var _ = await api.PostAsync($"api/runs/{id}/cancel", null); }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RunTerminal_ChildCanvasFollowsTheInvocationOfTheSameRun()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1560, Height = 1050 } });
        var graph = Example("phase07/subworkflow");
        graph.Nodes.Add(new("terminal", NodeTypes.RunTerminal, 1, new()));
        graph.Layout.Add("terminal", new(65, 330));
        await EditorTests.OpenAsync(page);
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "subflow.terminal.workflow.json", MimeType = "application/json",
            Buffer = JsonSerializer.SerializeToUtf8Bytes(graph, WorkflowDocument.Json)
        });
        var terminal = page.Locator("[data-kind='editor.run-terminal']");
        await terminal.Locator("[data-run-start]").ClickAsync();
        await Expect(terminal.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        var id = await terminal.Locator("[data-run-id]").InnerTextAsync();
        await terminal.Locator("[data-trace-event-kind=NodeCompleted][data-trace-node='first/output']").ClickAsync();
        await Expect(terminal.Locator("[data-run-id]")).ToHaveTextAsync(id);
        await Expect(terminal.Locator("[data-run-start]")).ToBeDisabledAsync();
        await Expect(terminal.Locator("[data-trace-node='second/output']")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-node=output]")).ToHaveAttributeAsync("data-inspected", "true");
        await Expect(page.Locator("[data-node=output] [data-run-output]")).ToHaveTextAsync("[release]");
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RunTerminal_PrintsChronologicalLogAndCopiesFullResizableOutput()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new()
        {
            Locale = "en-US",
            ViewportSize = new() { Width = 1560, Height = 1050 },
            Permissions = ["clipboard-read", "clipboard-write"]
        });
        await EditorTests.OpenAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "New workflow", Exact = true }).ClickAsync();
        var terminal = page.Locator("[data-kind='editor.run-terminal']");
        await Expect(terminal).ToHaveCountAsync(1);
        await Expect(terminal.Locator("[data-run-start]")).ToBeVisibleAsync();
        var io = page.Locator("[data-library-group=LibraryInputOutput]");
        await Expect(io.Locator("summary")).ToHaveTextAsync("Input / Output");
        await Expect(io.Locator("[data-library='core.text-input']")).ToBeVisibleAsync();
        await Expect(io.Locator("[data-library='core.output']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-library-group=LibraryWorkflow] [data-library='core.output']")).ToHaveCountAsync(0);
        var console = terminal.GetByRole(AriaRole.Log);
        await Expect(console).ToHaveTextAsync("Waiting for workflow events…");

        var payload = JsonSerializer.Serialize(new { device = "pump-01", status = new string('x', 1400) });
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "terminal.workflow.json", MimeType = "application/json",
            Buffer = JsonSerializer.SerializeToUtf8Bytes(TerminalGraph(payload), WorkflowDocument.Json)
        });
        await Expect(terminal).ToHaveCountAsync(1);
        await terminal.Locator("[data-run-step]").ClickAsync();
        await Expect(terminal.Locator("[data-run-state=Paused]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-node=trim]")).ToHaveAttributeAsync("data-node-state", "Idle");
        await terminal.Locator("[data-run-continue]").ClickAsync();
        await Expect(terminal.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(console.Locator("[data-trace-event]").First).ToContainTextAsync("Queued");
        await Expect(console.Locator("[data-trace-event]").Last).ToContainTextAsync("Succeeded");
        await Expect(console).ToHaveCSSAsync("font-family", new System.Text.RegularExpressions.Regex("monospace"));
        Assert.True(await console.EvaluateAsync<bool>("el => el.scrollHeight - el.scrollTop - el.clientHeight < 8"));
        var runId = await terminal.Locator("[data-run-id]").InnerTextAsync();
        var output = page.Locator("[data-node=output]");
        var result = output.Locator("[data-run-output]");
        await Expect(result).ToContainTextAsync("pump-01");
        await Expect(page.Locator("[data-inspect-run-node]")).ToHaveCountAsync(0);
        var handle = output.Locator("[data-output-resize]");
        var before = (await output.BoundingBoxAsync())!;
        var point = (await handle.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(point.X + 6, point.Y + 6);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(point.X + 170, point.Y + 104, new() { Steps = 8 });
        await page.Mouse.UpAsync();
        await Expect(output).ToHaveAttributeAsync("data-output-sized", "true");
        var after = (await output.BoundingBoxAsync())!;
        Assert.True(after.Width > before.Width + 60);
        Assert.True(after.Height > before.Height + 40);

        await output.Locator("[data-output-copy]").ClickAsync();
        await Expect(output.Locator("[data-output-copy-status]")).ToHaveTextAsync("Copied");
        Assert.Equal(payload, await page.EvaluateAsync<string>("navigator.clipboard.readText()"));
        await page.EvaluateAsync("navigator.clipboard.writeText('cleared')");
        await result.PressAsync("Control+c");
        await page.WaitForFunctionAsync("expected => navigator.clipboard.readText().then(text => text === expected)", payload);

        await terminal.Locator("[data-trace-event-kind=NodeCompleted][data-trace-node=output]").ClickAsync();
        await Expect(output).ToHaveAttributeAsync("data-inspected", "true");
        await Expect(terminal.Locator("[data-console-detail=NodeCompleted] [data-trace-value]")).ToContainTextAsync("pump-01");
        await page.Locator("[data-run-inspector]").ClickAsync();
        await page.Locator($"[data-history-run='{runId}']").ClickAsync();
        await Expect(terminal.Locator("[data-trace-mode]")).ToHaveTextAsync("Run history");
        await Expect(terminal.Locator("[data-trace-run]")).ToHaveTextAsync(runId);
        await Expect(terminal.Locator("[data-run-start]")).ToBeDisabledAsync();
        await Expect(terminal.Locator("input[type=range], [data-replay-next], [data-replay-prev]")).ToHaveCountAsync(0);
        await Expect(console.Locator("[data-trace-event]").Last).ToContainTextAsync("Succeeded");
        await Expect(result).ToContainTextAsync("pump-01");
        await terminal.Locator("[data-trace-live]").ClickAsync();
        if (WorkflowTests.EvidenceDirectory is { } directory)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Fit graph", Exact = true }).ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "run-terminal-console.png") });
        }
        var savedStyle = await output.GetAttributeAsync("style");
        await page.ReloadAsync();
        await Expect(output).ToHaveAttributeAsync("style", savedStyle!);
    }

    private static WorkflowDocument TerminalGraph(string payload) => new("0.1", "terminal-demo", 1,
        [new("input", NodeTypes.Input, 1, new(payload)), new("trim", NodeTypes.Normalize, 1, new()),
         new("output", NodeTypes.Output, 1, new()), new("terminal", NodeTypes.RunTerminal, 1, new())],
        [new("a", "input", "text", "trim", "in", "message"), new("b", "trim", "text", "output", "value", "message")],
        new() { ["input"] = new(70, 70), ["trim"] = new(355, 75), ["output"] = new(665, 75), ["terminal"] = new(64, 330) });
}
