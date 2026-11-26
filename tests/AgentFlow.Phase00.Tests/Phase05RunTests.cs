using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    [Fact]
    public async Task BatchStep_PausesBeforeNextDispatchAndKeepsParallelAgentsOverlapping()
    {
        using var api = Client();
        var graph = Example("parallel-review");
        var id = await StartAsync(api, new(graph, Guid.NewGuid(), new(PauseAtStart: true)));
        try
        {
            var pause = await WaitAsync(api, id, run => run.State == "Paused");
            Assert.False(pause.CanStepNode);
            Assert.Empty(Started(await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events")));
            using var invalid = await api.PostAsJsonAsync($"api/runs/{id}/control", new RunControlRequest("stepNode", pause.Sequence));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            await SendControlAsync(api, pause, "stepBatch");
            var next = await WaitAsync(api, id, run => run.State == "Paused" && run.Sequence > pause.Sequence);
            Assert.Equal(new[] { "input" }, Started(await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events")));
            using var stale = await api.PostAsJsonAsync($"api/runs/{id}/control", new RunControlRequest("continue", pause.Sequence));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await SendControlAsync(api, next, "continue");
            var result = await WaitAsync(api, id, Terminal);
            Assert.Equal("Succeeded", result.State);
            var trace = await GetAsync<RunRecording>(api, $"api/runs/{id}/recording");
            string[] agents = ["reliability", "performance", "maintenance"];
            var starts = trace.Events.Where(item => item.Kind == "NodeStarted" && agents.Contains(item.NodeId)).ToArray();
            var ends = trace.Events.Where(item => item.Kind == "NodeCompleted" && agents.Contains(item.NodeId)).ToArray();
            Assert.Equal(3, starts.Length);
            Assert.Equal(3, ends.Length);
            Assert.True(starts.Max(item => item.Sequence) < ends.Min(item => item.Sequence));
            Assert.Contains(trace.Events, item => item.Kind == "BatchCompleted" && item.Batch is not null);
            await WorkflowTests.EvidenceAsync("phase05-batch-step.json", trace);
        }
        finally { using var _ = await api.PostAsync($"api/runs/{id}/cancel", null); }
    }

    [Fact]
    public async Task NodeBreakpointAndPrefix_RunNoDownstreamNodeAndCacheOnlyOptedInTransforms()
    {
        using var api = Client();
        var graph = new WorkflowDocument("0.1", "debug-prefix", 1,
            [new("input", NodeTypes.Input, 1, new("  debug-data  ")), new("trim", NodeTypes.Normalize, 1, new()),
                new("output", NodeTypes.Output, 1, new())],
            [new("a", "input", "text", "trim", "in", "message"), new("b", "trim", "text", "output", "value", "message")], []);
        var id = await StartAsync(api, new(graph, Guid.NewGuid(), new(BreakpointNode: "trim", StopAfterNode: "trim", UsePureCache: true)));
        try
        {
            var pause = await WaitAsync(api, id, run => run.State == "Paused");
            Assert.True(pause.CanStepNode);
            Assert.Equal(new[] { "input" }, Started(await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events")));
            await SendControlAsync(api, pause, "stepNode");
            var final = await WaitAsync(api, id, Terminal);
            Assert.Equal("Succeeded", final.State);
            Assert.Equal("debug-data", final.Output);
            var trace = await GetAsync<RunRecording>(api, $"api/runs/{id}/recording");
            Assert.Contains(trace.Events, item => item is { Kind: "Paused", NodeId: "trim" });
            Assert.Contains(trace.Events, item => item is { Kind: "NodeSkipped", NodeId: "output" });
            Assert.DoesNotContain("output", Started(trace.Events));
            Assert.Equal("trim", trace.Definition.Debug?.StopAfterNode);
            await WorkflowTests.EvidenceAsync("phase05-prefix.json", trace);
        }
        finally { using var _ = await api.PostAsync($"api/runs/{id}/cancel", null); }

        var cached = await StartAsync(api, new(graph, Guid.NewGuid(), new(UsePureCache: true)));
        await WaitAsync(api, cached, Terminal);
        Assert.Contains(await GetAsync<RunEventItem[]>(api, $"api/runs/{cached}/events"),
            item => item is { Kind: "NodeCached", NodeId: "trim" });
        var (_, noCache) = await ExecuteAdvancedAsync(api, graph);
        Assert.DoesNotContain(noCache, item => item.Kind == "NodeCached");
        var model = await StartAsync(api, new(Graph("must re-evaluate"), Guid.NewGuid(), new(UsePureCache: true)));
        await WaitAsync(api, model, Terminal);
        Assert.DoesNotContain(await GetAsync<RunEventItem[]>(api, $"api/runs/{model}/events"), item => item.Kind == "NodeCached");

        using var rejected = await api.PostAsJsonAsync("api/runs", new RunRequest(Example("parallel-review"), Guid.NewGuid(),
            new(BreakpointNode: "merge")));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("DebugRequiresLinearGraph", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recording_PreservesErrorUsageAndArtifactsAndScrubsSplitCredentials()
    {
        using var api = Client();
        const string fakeKey = "phase03-credential-regression-871108";
        var input = new string('x', 1092) + fakeKey + "-end";
        var id = await StartAsync(api, new(Graph(input), Guid.NewGuid()));
        var final = await WaitAsync(api, id, Terminal);
        Assert.Equal("Succeeded", final.State);
        Assert.DoesNotContain(fakeKey, final.Output);
        var recording = await GetAsync<RunRecording>(api, $"api/runs/{id}/recording");
        var body = Assert.Single(recording.Events, item => item is { Kind: "NodeStarted", NodeId: "agent" }).Value;
        Assert.NotNull(body);
        var artifact = Assert.IsType<Guid>(body.ArtifactId);
        var full = await api.GetStringAsync($"api/runs/{id}/artifacts/{artifact}");
        Assert.Equal(input.Replace(fakeKey, "[REDACTED]", StringComparison.Ordinal), full);
        Assert.DoesNotContain(fakeKey, string.Concat(recording.Events.Where(item => item.Kind == "OutputDelta").Select(item => item.Text)));
        var report = await api.GetStringAsync($"api/runs/{id}/report");
        Assert.DoesNotContain(fakeKey, report);
        Assert.Contains("[REDACTED]", report);
        Assert.Contains("ModelRequest", report);
        Assert.Contains(recording.Events, item => item is { Kind: "ModelResult", Usage.TotalTokens: > 0 });

        var invalid = Example("score-review");
        SetText(invalid, "input", """{"review":{"score":"wrong"}}""");
        var (failed, events) = await ExecuteAdvancedAsync(api, invalid);
        Assert.Equal("InvalidStructuredOutput", failed.Error);
        Assert.Contains(events, item => item is { Kind: "NodeFailed", NodeId: "structure", Text: "InvalidStructuredOutput", DurationMs: >= 0 });
        await WorkflowTests.EvidenceAsync("phase05-failure.json", new { failed, events });
    }

    [Fact]
    public async Task Inspector_RecordsAnObservedProviderToolCall()
    {
        using var api = Client();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = new ModelProfile("phase05-tool-stub", $"http://127.0.0.1:{port}/v1", "tool-trace",
            TimeoutSeconds: 5, Streaming: false);
        await SaveAsync(api, profile);
        const string reply = """
            {"id":"trace-tool","object":"chat.completion","created":1,"model":"tool-trace","choices":[{"index":0,"message":{"role":"assistant","content":"observed","tool_calls":[{"id":"tool-1","type":"function","function":{"name":"fixture_tool","arguments":"{\"value\":\"fixture\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}
            """;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sent = ServeModelAsync(listener, [200], deadline.Token, reply);
        var (run, events) = await ExecuteAdvancedAsync(api, Graph("observe this response", profile.Id));
        await sent;
        Assert.Equal("Succeeded", run.State);
        var tool = Assert.Single(events, item => item is { Kind: "ToolCall", NodeId: "agent" });
        Assert.Equal("fixture_tool", tool.Text);
        using var args = JsonDocument.Parse(tool.Value!.Preview);
        Assert.Equal("fixture", args.RootElement.GetProperty("value").GetString());
        Assert.DoesNotContain(events, item => item.Kind is "ToolRequest" or "ToolResult" or "HumanWaiting");
        await WorkflowTests.EvidenceAsync("phase05-tool-observed.json", new { run, events });
    }

    [Fact]
    public async Task RunArchive_ReplaysAfterHostRestartAndDoesNotRestartAnInterruptedRequest()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(folder.FullName, "AgentFlow.slnx")))
            folder = folder.Parent ?? throw new DirectoryNotFoundException("AgentFlow.slnx");
        var root = folder.FullName;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(root, "src", "AgentFlow.NodeHost", "bin", configuration, "net10.0", "AgentFlow.NodeHost.dll");
        var directory = Path.Combine(root, "work", "phase05-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var address = $"http://127.0.0.1:{port}";
        using var api = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };
        var completed = new RunRequest(Example("score-review"), Guid.NewGuid());
        var interrupted = new RunRequest(Graph("interrupted"), Guid.NewGuid(), new(PauseAtStart: true));
        RunRecording before;
        using (var first = StartHost())
        try
        {
            await ReadyAsync(api);
            await StartAsync(api, completed);
            Assert.Equal("Succeeded", (await WaitAsync(api, completed.RequestId, Terminal)).State);
            before = await GetAsync<RunRecording>(api, $"api/runs/{completed.RequestId}/recording");
            await StartAsync(api, interrupted);
            Assert.Equal("Paused", (await WaitAsync(api, interrupted.RequestId, run => run.State == "Paused")).State);
        }
        finally { StopHost(first); }

        using var second = StartHost();
        try
        {
            await ReadyAsync(api);
            var summaries = await GetAsync<RunSummary[]>(api, "api/runs?state=Succeeded&q=score-review");
            Assert.Equal(completed.RequestId, Assert.Single(summaries).RunId);
            var after = await GetAsync<RunRecording>(api, $"api/runs/{completed.RequestId}/recording");
            Assert.Equal(before.Events, after.Events);
            Assert.Equal("approved", after.Snapshot.Output);
            Assert.Equal(interrupted.RequestId, await StartAsync(api, interrupted));
            var aborted = await GetAsync<RunRecording>(api, $"api/runs/{interrupted.RequestId}/recording");
            Assert.Equal("RunInterrupted", aborted.Snapshot.Error);
            Assert.DoesNotContain(aborted.Events, item => item.Kind == "NodeStarted");
            await WorkflowTests.EvidenceAsync("phase05-reopened-history.json", after);
        }
        finally { StopHost(second); }

        Process StartHost()
        {
            var options = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            options.ArgumentList.Add(dll);
            options.ArgumentList.Add("--urls");
            options.ArgumentList.Add(address);
            options.Environment["AGENTFLOW_RUN_DATA"] = directory;
            var process = Process.Start(options) ?? throw new InvalidOperationException("Could not start archive test host.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task Debugger_CanvasPausesInspectsAndReadsSavedRunWithoutSubmittingAgain()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1540, Height = 1000 } });
        await EditorTests.OpenAsync(page);
        await page.Locator("input[type=file]").SetInputFilesAsync(Path.Combine(AppContext.BaseDirectory, "chat.workflow.json"));
        await page.Locator("[data-debug-options]").ClickAsync();
        await page.Locator("[data-debug-pause]").CheckAsync();
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Paused]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-node=input]")).ToHaveAttributeAsync("data-node-state", "Paused");
        var id = await page.Locator("[data-run-id]").InnerTextAsync();
        await page.Locator("[data-run-inspector]").ClickAsync();
        await page.Locator("[data-trace-tab=queue]").ClickAsync();
        await Expect(page.Locator($"[data-history-run='{id}']")).ToBeVisibleAsync();
        await page.Locator("[data-trace-tab=timeline]").ClickAsync();
        await page.Locator("[data-run-step]").ClickAsync();
        await Expect(page.Locator("[data-node=input]")).ToHaveAttributeAsync("data-node-state", "Completed");
        await Expect(page.Locator("[data-node=agent]")).ToHaveAttributeAsync("data-node-state", "Idle");
        await page.Locator("[data-run-continue]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await page.Locator("[data-trace-inspect]").SelectOptionAsync("agent");
        await Expect(page.Locator("[data-node=agent]")).ToHaveAttributeAsync("data-inspected", "true");
        await Expect(page.Locator("[data-node-inspector=agent] [data-trace-value=NodeStarted]")).ToContainTextAsync("READY");
        await Expect(page.Locator("[data-node-inspector=agent] [data-trace-value=NodeCompleted]")).ToContainTextAsync("Mock:");
        await page.Locator("[data-inspected-kind=ModelRequest] summary").ClickAsync();
        var requestPayload = page.Locator("[data-trace-value=ModelRequest]");
        await Expect(requestPayload).ToBeVisibleAsync();
        using var messages = JsonDocument.Parse(await requestPayload.InnerTextAsync());
        Assert.Equal("Follow the user's instruction and answer briefly.",
            messages.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        await page.Locator("[data-trace-tab=history]").ClickAsync();
        await page.Locator("[data-run-query]").FillAsync(id);
        await page.Locator("[data-run-query-submit]").ClickAsync();
        await page.Locator($"[data-history-run='{id}']").ClickAsync();
        await Expect(page.Locator("[data-trace-mode]")).ToHaveTextAsync("历史日志");
        await Expect(page.Locator("[data-trace-run]")).ToHaveTextAsync(id);
        await Expect(page.Locator("[data-trace-replay]")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-node=agent]")).ToHaveAttributeAsync("data-node-state", "Completed");
        await page.Locator("[data-trace-inspect]").SelectOptionAsync("agent");
        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("[data-trace-export]").ClickAsync());
        var exported = await File.ReadAllTextAsync(await download.PathAsync());
        var report = JsonSerializer.Deserialize<RunDiagnosticReport>(exported, WorkflowDocument.Json);
        Assert.Equal(Guid.Parse(id), report!.Recording.Snapshot.RunId);
        Assert.Single(report.Recording.Events, item => item.Kind == "ModelRequest");
        await Expect(page.Locator("[data-run-id]")).ToHaveTextAsync(id);
        if (WorkflowTests.EvidenceDirectory is { } output)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "phase05-diagnostics-export.json"), exported);
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase05-history.png") });
            await page.Locator("[data-node-inspector=agent]").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase05-inspector.png") });
        }
    }

    private static async Task SendControlAsync(HttpClient api, RunSnapshot paused, string command)
    {
        using var response = await api.PostAsJsonAsync($"api/runs/{paused.RunId}/control", new RunControlRequest(command, paused.Sequence));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task ReadyAsync(HttpClient api)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            try
            {
                using var response = await api.GetAsync("api/models", deadline.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(150, deadline.Token);
        }
    }

    private static void StopHost(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }
}
