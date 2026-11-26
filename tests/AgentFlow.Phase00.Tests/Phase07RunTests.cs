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
    [Trait("Category", "RunHost")]
    public async Task Subworkflow_UsesLockedRevisionAndIndependentInvocations()
    {
        using var api = Client();
        var graph = Example("phase07/subworkflow");
        var child = Assert.Single(graph.Subworkflows!);
        var newer = child with { Revision = 2, Nodes = child.Nodes.ToList() };
        SetText(newer, "prompt", "new:{{input}}");
        graph.Subworkflows!.Add(newer);
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Succeeded", run.State);
        Assert.Equal("[[release]]", run.Output);
        Assert.Equal(new[] { "first/input", "second/input" }, Started(events).Where(id => id?.EndsWith("/input") == true));
        await WorkflowTests.EvidenceAsync("phase07-subworkflow-events.json", events);

        graph.Subworkflows.Remove(child);
        await AssertRejectedAsync(api, graph, "SubworkflowVersionMissing");
        await AssertRejectedAsync(api, graph with { Subworkflows = [newer with { WorkflowId = graph.WorkflowId }] }, "SubworkflowRecursion");
    }

    [Fact]
    public void Encapsulation_PreservesConnectionsAndReferencedDefinitions()
    {
        var graph = Example("phase07/subworkflow");
        var wrapped = SubworkflowEditing.Encapsulate(graph, new HashSet<string> { "first", "second" }, "wrapper");
        var child = Assert.Single(wrapped.Subworkflows!, item => item.WorkflowId == "wrapper");
        Assert.Equal("transform-text", Assert.Single(child.Subworkflows!).WorkflowId);
        Assert.Equal(new[] { "input", "output", "wrapper" }, wrapped.Nodes.Select(node => node.Id));
        Assert.Contains(wrapped.Edges, edge => edge is { Source: "input", Target: "wrapper", TargetPort: "in" });
        Assert.Contains(wrapped.Edges, edge => edge is { Source: "wrapper", SourcePort: "text", Target: "output" });
        Assert.Contains(child.Edges, edge => edge is { Source: "wrapper-in", SourcePort: "text", Target: "first" });
        _ = WorkflowDocument.Read(JsonSerializer.Serialize(wrapped, WorkflowDocument.Json));
    }

    [Theory]
    [InlineData(NodeTypes.Agent, "ChatClientAgent")]
    [InlineData(NodeTypes.HarnessAgent, "HarnessAgent")]
    [Trait("Category", "RunHost")]
    public async Task HumanWait_ResumesAcrossProcessRestartsAndReplaysSimulationWithoutAnotherWrite(string agentType, string agentName)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(folder.FullName, "AgentFlow.slnx")))
            folder = folder.Parent ?? throw new DirectoryNotFoundException("AgentFlow.slnx");
        var root = folder.FullName;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(root, "src", "AgentFlow.NodeHost", "bin", configuration, "net10.0", "AgentFlow.NodeHost.dll");
        var data = Path.Combine(root, "work", "phase07-restart-" + Guid.NewGuid().ToString("N"));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
        reservation.Stop();
        using var api = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };
        var graph = Example("phase07/approval");
        var chat = Graph("Release draft 42");
        graph.Nodes.AddRange(chat.Nodes.Where(node => node.TypeId is NodeTypes.Agent or NodeTypes.Model)
            .Select(node => node.TypeId == NodeTypes.Agent ? node with { TypeId = agentType } : node));
        graph.Edges[0] = graph.Edges[0] with { Target = "agent" };
        graph.Edges.Add(Assert.Single(chat.Edges, edge => edge.Kind == "resource"));
        graph.Edges.Add(new("agent-approval", "agent", "text", "approval", "in", "message"));
        var request = new RunRequest(graph, Guid.NewGuid());
        var drift = request with { RequestId = Guid.NewGuid() };
        var archivePath = Path.Combine(data, "runs", $"{request.RequestId:N}.json");
        HumanWait wait;
        using (var host = StartHost())
        try
        {
            await ReadyAsync(api);
            await StartAsync(api, request);
            wait = (await WaitAsync(api, request.RequestId, run => run.State == "Waiting" || Terminal(run))).Waiting!;
            Assert.NotNull(wait);
            Assert.Equal("Mock: Release draft 42", wait.Preview);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(data, "runs", $"{request.RequestId:N}.checkpoints")));
            await StartAsync(api, drift);
            Assert.Equal("Waiting", (await WaitAsync(api, drift.RequestId, run => run.State == "Waiting" || Terminal(run))).State);
        }
        finally { StopHost(host); }

        var actionsPath = $"api/runs/{request.RequestId}/decision";
        var approve = new HumanDecision(wait.RequestId, "operator", "approve");
        var ledger = Path.Combine(data, "runs", "simulated-effects");
        string acceptedArchive;
        string output;
        using (var host = StartHost())
        try
        {
            await ReadyAsync(api);
            Assert.Equal(wait, (await GetAsync<RunSnapshot>(api, $"api/runs/{request.RequestId}")).Waiting);
            using var wrong = await api.PostAsJsonAsync(actionsPath, approve with { Actor = "someone-else" });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            var blocker = await StartAsync(api, new(Graph("checkpoint test queue gate"), Guid.NewGuid(), new(PauseAtStart: true)));
            var paused = await WaitAsync(api, blocker, run => run.State == "Paused");
            using var accepted = await api.PostAsJsonAsync(actionsPath, approve);
            accepted.EnsureSuccessStatusCode();
            Assert.Equal("Queued", (await GetAsync<RunSnapshot>(api, $"api/runs/{request.RequestId}")).State);
            acceptedArchive = await File.ReadAllTextAsync(archivePath);
            await SendControlAsync(api, paused, "continue");
            var result = await WaitAsync(api, request.RequestId, Terminal);
            Assert.Equal("Succeeded", result.State);
            output = result.Output;
            Assert.Single(Directory.GetFiles(ledger, "*.json"));
            using var duplicate = await api.PostAsJsonAsync(actionsPath, approve);
            duplicate.EnsureSuccessStatusCode();
            using var conflict = await api.PostAsJsonAsync(actionsPath, approve with { Action = "reject" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally { StopHost(host); }

        // 保留首次执行写入的台账，恢复实际落盘的决定，模拟写后尚未归档终态。
        await File.WriteAllTextAsync(archivePath, acceptedArchive);
        using (var host = StartHost())
        try
        {
            await ReadyAsync(api);
            var replayed = await WaitAsync(api, request.RequestId, Terminal);
            Assert.Equal("Succeeded", replayed.State);
            Assert.Equal(output, replayed.Output);
            Assert.Single(Directory.GetFiles(ledger, "*.json"));
            var recording = await GetAsync<RunRecording>(api, $"api/runs/{request.RequestId}/recording");
            Assert.Single(Started(recording.Events), node => node == "input");
            var model = Assert.Single(recording.Events, item => item is { Kind: "ModelRequest", NodeId: "agent" });
            Assert.Contains("MAF.AIAgentBinding", model.Value!.Preview);
            Assert.Contains(agentName, model.Value.Preview);
            Assert.Single(recording.Events, item => item is { Kind: "HumanDecided", NodeId: "approval" });
            Assert.Single(recording.Events, item => item is { Kind: "ToolResult", NodeId: "write" });
            await WorkflowTests.EvidenceAsync(agentType == NodeTypes.HarnessAgent
                ? "phase07-harness-recovered-approval.json" : "phase07-recovered-approval.json", recording);
        }
        finally { StopHost(host); }

        using (var host = StartHost(simulation: false))
        try
        {
            await ReadyAsync(api);
            var incompatible = await GetAsync<RunSnapshot>(api, $"api/runs/{drift.RequestId}");
            Assert.Equal("CheckpointIncompatible", incompatible.Error);
        }
        finally { StopHost(host); }

        Process StartHost(bool simulation = true)
        {
            var options = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { dll, "--urls", address })
                options.ArgumentList.Add(argument);
            options.Environment["AGENTFLOW_RUN_DATA"] = data;
            options.Environment["AGENTFLOW_SIMULATED_WRITES"] = simulation ? "1" : "0";
            var process = Process.Start(options) ?? throw new InvalidOperationException("Could not start checkpoint test host.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
    }

    [Theory]
    [InlineData("submit", "Succeeded", null)]
    [InlineData("reject", "Failed", "ApprovalRejected")]
    [InlineData(null, "TimedOut", "HumanWaitExpired")]
    [Trait("Category", "RunHost")]
    public async Task HumanInput_HandlesSubmissionRejectionAndExpiration(string? action, string state, string? error)
    {
        using var api = Client();
        var graph = Example("phase07/input");
        if (action is null)
            graph.Nodes[1] = graph.Nodes[1] with { Config = Parameters(new { assignee = "operator", prompt = "Title", seconds = 1 }) };
        else
            graph.Nodes[1].Config.Parameters!["assignee"] = JsonSerializer.SerializeToElement(" operator ");
        var id = await StartAsync(api, new(graph, Guid.NewGuid()));
        if (action is not null)
        {
            var snapshot = await WaitAsync(api, id, run => run.State == "Waiting" || Terminal(run));
            Assert.NotNull(snapshot.Waiting);
            Assert.Equal("operator", snapshot.Waiting.Assignee);
            var value = action == "submit" ? "release #42" : null;
            using var response = await api.PostAsJsonAsync($"api/runs/{id}/decision", new HumanDecision(snapshot.Waiting.RequestId, "operator", action, value));
            response.EnsureSuccessStatusCode();
        }
        var result = await WaitAsync(api, id, Terminal);
        Assert.Equal(state, result.State);
        Assert.Equal(error, result.Error);
        Assert.Equal(action == "submit" ? "release #42" : "", result.Output);
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task HumanWait_YieldsWorkerAndMaintainsStateAcrossTwoRequests()
    {
        using var api = Client();
        var graph = Example("phase07/input");
        var approval = Example("phase07/approval").Nodes[1];
        graph.Nodes.Add(approval);
        graph.Edges[1] = graph.Edges[1] with { Target = "approval", TargetPort = "in" };
        graph.Edges.Add(new("approval-output", "approval", "text", "output", "value", "message"));
        var id = await StartAsync(api, new(graph, Guid.NewGuid()));
        try
        {
            var first = (await WaitAsync(api, id, run => run.State == "Waiting" || Terminal(run))).Waiting;
            Assert.NotNull(first);
            var (unrelated, _) = await ExecuteAdvancedAsync(api, Example("phase07/subworkflow"));
            Assert.Equal("Succeeded", unrelated.State);
            var path = $"api/runs/{id}/decision";
            using var submitted = await api.PostAsJsonAsync(path, new HumanDecision(first.RequestId, "operator", "submit", "updated draft"));
            submitted.EnsureSuccessStatusCode();
            var second = (await WaitAsync(api, id, run => run is { State: "Waiting", Waiting.NodeId: "approval" } || Terminal(run))).Waiting;
            Assert.NotNull(second);
            Assert.NotEqual(first.RequestId, second.RequestId);
            Assert.Equal("updated draft", second.Preview);
            using var approved = await api.PostAsJsonAsync(path, new HumanDecision(second.RequestId, "operator", "approve"));
            approved.EnsureSuccessStatusCode();
            var finished = await WaitAsync(api, id, Terminal);
            Assert.Equal("Succeeded", finished.State);
            Assert.Equal("updated draft", finished.Output);
        }
        finally { using var _ = await api.PostAsync($"api/runs/{id}/cancel", null); }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task Phase07_CanvasPublishesOnlySelectedVersionAndRunPanelAcceptsHumanInput()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1600, Height = 1000 } });
        await EditorTests.OpenAsync(page);
        await EditorTests.LoadExampleAsync(page, "subworkflow");
        await Expect(page.Locator("[data-node=first] [data-port=in]")).ToContainTextAsync("Source");
        await page.Locator("[data-node=first] .node-handle strong").DblClickAsync();
        await Expect(page.Locator("[aria-current=page]")).ToHaveTextAsync("transform-text · r1");
        var prompt = page.Locator("[data-node=prompt] textarea");
        await prompt.FillAsync("new:{{input}}");
        await page.Locator("[data-publish-subworkflow]").ClickAsync();
        await Expect(page.Locator("[data-node=first] [data-parameter=revision]")).ToHaveValueAsync("2");
        await Expect(page.Locator("[data-node=second] [data-parameter=revision]")).ToHaveValueAsync("1");
        await page.Locator("[data-node=first] .node-handle strong").ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator("[data-extract-subworkflow]").ClickAsync();
        await Expect(page.Locator("[data-node=first]")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-kind='flow.subworkflow']")).ToHaveCountAsync(2);
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase07-versioned-subflow.png") });

        await EditorTests.LoadExampleAsync(page, "input");
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Waiting]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-node=request]")).ToHaveAttributeAsync("data-node-state", "Waiting");
        await Expect(page.Locator("[data-human-assignee]")).ToHaveTextAsync("operator");
        if (WorkflowTests.EvidenceDirectory is { } waitOutput)
            await page.ScreenshotAsync(new() { Path = Path.Combine(waitOutput, "phase07-human-input.png") });
        await page.Locator("[data-human-actor]").FillAsync("operator");
        await page.Locator("[data-human-value]").FillAsync("发布候选版本");
        await page.Locator("[data-human-submit]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-run-output]")).ToHaveTextAsync("发布候选版本");
    }
}
