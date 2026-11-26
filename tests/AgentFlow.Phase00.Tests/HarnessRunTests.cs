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
    public async Task McpResources_SaveAuthorizationsAcrossRestartAndRejectInvalidEndpoints()
    {
        var (options, client) = CreateConversationHost();
        using var api = client;
        const string route = "api/tools/mcp-connections";
        var profile = new McpProfile("managed-fixture", "http://127.0.0.1:5190/mcp", ["read_device"]);
        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            using var invalid = await api.PostAsJsonAsync(route, profile with { Url = "http://169.254.169.254/mcp" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var saved = await api.PostAsJsonAsync(route + "?createOnly=true", profile);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var duplicate = await api.PostAsJsonAsync(route + "?createOnly=true", profile);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        finally { StopHost(host); }

        using (var host = StartConversationHost(options))
        try
        {
            await ReadyAsync(api);
            var saved = Assert.Single(await GetAsync<McpProfile[]>(api, route), item => item.Id == profile.Id);
            Assert.Equal(profile.Url, saved.Url);
            Assert.Equal(profile.Tools, saved.Tools);
        }
        finally { StopHost(host); }
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task HarnessResources_UseNativeInstructionsAndValidateTheContextWindow()
    {
        using var fixture = new TcpListener(IPAddress.Loopback, 0);
        fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (options, client) = CreateConversationHost();
        using var api = client;
        using var host = StartConversationHost(options);
        const string profileId = "harness-settings-fixture";
        try
        {
            await ReadyAsync(api);
            await SaveAsync(api, new(profileId, $"http://{fixture.LocalEndpoint}/v1", "fixture", Streaming: false));
            var graph = Graph("Current request", profileId);
            var agent = UseHarness(graph);
            AddHarnessResource(graph, agent.Id, "guidance", NodeTypes.HarnessInstructions, "instructions", new("Harness guidance marker."));
            AddHarnessResource(graph, agent.Id, "loop", NodeTypes.HarnessFunctionLoop, "loop",
                new(Parameters: new() { ["maximumIterations"] = JsonSerializer.SerializeToElement(3) }));
            var settings = new Dictionary<string, JsonElement> { ["contextWindowTokens"] = JsonSerializer.SerializeToElement(128) };
            AddHarnessResource(graph, agent.Id, "compaction", NodeTypes.HarnessCompaction, "compaction", new(Parameters: settings));
            using var invalid = await api.PostAsJsonAsync("api/runs", new RunRequest(graph, Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("HarnessContextTooSmall", await invalid.Content.ReadAsStringAsync());

            using var window = JsonDocument.Parse("8192.0");
            settings["contextWindowTokens"] = window.RootElement.Clone();
            var runId = await StartAsync(api, new(graph, Guid.NewGuid()));
            var messages = await ConversationModelReplyAsync(fixture, "READY", deadline.Token);
            var instructions = string.Join("\n", messages.Where(message => message.Role is "system" or "developer")
                .Select(message => message.Content));
            Assert.Contains("Harness guidance marker.", instructions);
            Assert.Contains(agent.Config.Text!, instructions);
            Assert.Equal("READY", (await WaitAsync(api, runId, Terminal)).Output);
            var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{runId}/events");
            using var captured = JsonDocument.Parse(Assert.Single(events, entry => entry.Kind == "ModelRequest").Value!.Preview);
            var harness = captured.RootElement.GetProperty("harness");
            Assert.Equal(3, harness.GetProperty("maximumIterations").GetInt32());
            Assert.Equal(8192, harness.GetProperty("contextWindowTokens").GetInt32());
            Assert.Equal("loop", harness.GetProperty("resourceNodes").GetProperty(NodeTypes.HarnessFunctionLoop).GetString());
        }
        finally { StopHost(host); }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task HarnessCanvas_GroupsCapabilitiesAndBindsAnAuthorizedToolOnTheCanvas()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1580, Height = 1000 } });
        await EditorTests.OpenAsync(page);
        var library = page.Locator("[data-library-group=LibraryHarness]");
        await Expect(library.Locator("[data-library]")).ToHaveCountAsync(5);
        await Expect(page.Locator("[data-library-group=LibraryTools] [data-library='tool.mcp']")).ToHaveCountAsync(1);
        await library.Locator("summary").ClickAsync();
        await Expect(library.Locator("[data-library='ai.harness-agent']")).ToBeHiddenAsync();
        await library.Locator("summary").ClickAsync();
        await page.Locator("[data-models-toggle]").ClickAsync();
        var manager = page.Locator("#model-manager");
        await Expect(manager.Locator("[data-model-profile=mock]")).ToBeVisibleAsync();
        await manager.Locator("[data-resource-tab=mcp]").ClickAsync();
        await Expect(manager.Locator("[data-mcp-new]")).ToHaveTextAsync("新建 MCP 连接");
        await manager.Locator("[data-mcp-new]").ClickAsync();
        var profileId = $"canvas-{Guid.NewGuid():N}"[..20];
        await manager.Locator("[data-mcp-id]").FillAsync(profileId);
        await manager.Locator("[data-mcp-url]").FillAsync("http://127.0.0.1:5190/mcp");
        await manager.Locator("[data-mcp-url]").BlurAsync();
        await manager.Locator("[data-mcp-discover]").ClickAsync();
        await Expect(manager.Locator("[data-mcp-allow=read_device]")).ToBeVisibleAsync();
        await Expect(manager.Locator("[data-mcp-allow=write_device]")).ToHaveCountAsync(0);
        await manager.Locator("[data-mcp-discovery] summary").ClickAsync();
        await Expect(manager.Locator("[data-mcp-discovery] pre")).ToContainTextAsync("deviceId");
        if (WorkflowTests.EvidenceDirectory is { } resources)
            await page.ScreenshotAsync(new() { Path = Path.Combine(resources, "phase07-mcp-resource-manager.png") });
        await manager.Locator("[data-mcp-allow=read_device]").CheckAsync();
        await manager.Locator("[data-mcp-save]").ClickAsync();
        await Expect(manager.Locator("[data-mcp-feedback=ProfileSaved]")).ToBeVisibleAsync();
        await manager.Locator("[data-model-close]").ClickAsync();
        await EditorTests.LoadExampleAsync(page, "harness-collaboration");
        await page.GetByRole(AriaRole.Button, new() { Name = "适应画布", Exact = true }).ClickAsync();
        var agent = page.Locator("[data-node=harness]");
        await Expect(agent.Locator("[data-resource]")).ToHaveCountAsync(4);
        await Expect(agent.Locator("[data-parameter]:not([data-parameter=text])")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-node=triage] [data-harness-tool-select]")).ToBeVisibleAsync();
        var iterations = page.Locator("[data-node=function-loop] [data-parameter=maximumIterations]");
        await Expect(iterations).ToHaveValueAsync("5");
        await iterations.FillAsync("4");
        await iterations.PressAsync("Tab");
        var tools = page.Locator("[data-node=toolset]");
        await tools.Locator("[data-harness-remove]").ClickAsync();
        await Expect(tools.Locator("[data-mcp-discover]")).ToHaveCountAsync(0);
        var reference = $"{profileId}/read_device";
        await tools.Locator("[data-harness-tool-select]").SelectOptionAsync(reference);
        await tools.Locator("[data-harness-add]").ClickAsync();
        await Expect(tools.Locator("[data-harness-bindings]")).ToContainTextAsync(reference);
        await page.GetByRole(AriaRole.Button, new() { Name = "适应画布", Exact = true }).ClickAsync();
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase07-harness-resource-canvas.png") });
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-node=output] [data-run-output]")).ToContainTextAsync("pump-01");
        var id = Guid.Parse(await page.Locator("[data-run-id]").InnerTextAsync());
        using var api = Client();
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        using var diagnostic = JsonDocument.Parse(Assert.Single(events, item => item is { Kind: "ModelRequest", NodeId: "harness" }).Value!.Preview);
        Assert.Equal(4, diagnostic.RootElement.GetProperty("harness").GetProperty("maximumIterations").GetInt32());
        Assert.Contains(events, item => item.Kind == "ToolResult" && item.Text == reference);
        await page.Locator("[data-models-toggle]").ClickAsync();
        await manager.Locator($"[data-mcp-profile={profileId}]").ClickAsync();
        await manager.Locator("[data-mcp-delete]").ClickAsync();
        await manager.Locator("[data-mcp-confirm-delete]").ClickAsync();
        await Expect(manager.Locator("[data-mcp-feedback=McpProfileDeleted]")).ToBeVisibleAsync();
        await manager.Locator("[data-model-close]").ClickAsync();
        await Expect(tools.Locator("[data-mcp-unavailable]")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task HarnessCollaboration_UsesBothNativeAgentsAndTheApprovedReadOnlyTool()
    {
        using var api = Client();
        var (run, events) = await ExecuteAdvancedAsync(api, Example("phase07/harness-collaboration"));
        Assert.True(run.State == "Succeeded", $"{run.State} ({run.Error})");
        Assert.Contains("pump-01", run.Output);
        Assert.Contains("72.5", run.Output);
        AssertHarnessCollaboration(events);
        await WorkflowTests.EvidenceAsync("phase07-harness-mock.json", new { run, events });
    }

    [Fact]
    public async Task HarnessCollaboration_LeavesSimpleBranchFreeOfModelAndToolCalls()
    {
        using var api = Client();
        var graph = Example("phase07/harness-collaboration");
        SetText(graph, "input", """{"deviceId":"pump-01","needsTool":false}""");
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Succeeded", run.State);
        Assert.Equal("No live device check requested.", run.Output);
        Assert.DoesNotContain(events, item => item.Kind is "ModelRequest" or "ToolRequest");
        Assert.Contains(events, item => item is { Kind: "NodeSkipped", NodeId: "harness" });
    }

    [Fact]
    public async Task HarnessCollaboration_RejectsUnapprovedToolsAndIncompatibleDebugExecution()
    {
        using var api = Client();
        var graph = Example("phase07/harness-collaboration");
        using var invalid = await api.PostAsJsonAsync("api/runs", new RunRequest(graph, Guid.NewGuid(), new(PauseAtStart: true)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("HarnessRequiresNativeExecution", await invalid.Content.ReadAsStringAsync());

        var node = graph.Nodes.Single(item => item.Id == "toolset");
        var config = node.Config with { Parameters = new(node.Config.Parameters!)
        {
            ["tools"] = JsonSerializer.SerializeToElement(new[] { "devices/write_device" })
        } };
        graph.Nodes[graph.Nodes.IndexOf(node)] = node with { Config = config };
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Failed", run.State);
        Assert.Equal("ToolAccessDenied", run.Error);
        Assert.Contains(events, item => item is { Kind: "ToolRejected", NodeId: "harness", Text: "devices/write_device" });
        Assert.DoesNotContain(events, item => item is { Kind: "ToolRequest", NodeId: "harness" });
        Assert.DoesNotContain(events, item => item is { Kind: "ModelRequest", NodeId: "harness" });
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task HarnessResources_EnforceTheConnectedToolCallBudget()
    {
        using var api = Client();
        using var fixture = new TcpListener(IPAddress.Loopback, 0);
        fixture.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        const string profileId = "harness-budget-fixture";
        await SaveAsync(api, new(profileId, $"http://{fixture.LocalEndpoint}/v1", "fixture", Streaming: false));
        var graph = Graph("Read device status", profileId);
        var agent = UseHarness(graph);
        AddHarnessResource(graph, agent.Id, "toolset", NodeTypes.HarnessMcpTools, "tools", new(Parameters: new()
        {
            ["tools"] = JsonSerializer.SerializeToElement(new[] { "devices/read_device" })
        }));
        AddHarnessResource(graph, agent.Id, "loop", NodeTypes.HarnessFunctionLoop, "loop", new(Parameters: new()
        {
            ["maxToolCalls"] = JsonSerializer.SerializeToElement(1)
        }));
        const string twoCalls = """
            {"id":"test","object":"chat.completion","created":1,"model":"fixture","choices":[{"index":0,"message":{"role":"assistant","tool_calls":[{"id":"one","type":"function","function":{"name":"devices__read_device","arguments":"{\"deviceId\":\"pump-01\"}"}},{"id":"two","type":"function","function":{"name":"devices__read_device","arguments":"{\"deviceId\":\"pump-02\"}"}}]},"finish_reason":"tool_calls"}]}
            """;
        var reply = ServeModelAsync(fixture, [200], deadline.Token, twoCalls);
        var id = await StartAsync(api, new(graph, Guid.NewGuid()));
        await reply;
        await ConversationModelReplyAsync(fixture, "READY", deadline.Token);
        var run = await WaitAsync(api, id, Terminal);
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        Assert.Equal("READY", run.Output);
        Assert.Equal(2, events.Count(item => item.Kind == "ToolRequest"));
        Assert.Single(events, item => item.Kind == "ToolResult");
        Assert.Contains(events, item => item.Kind == "ToolRejected" && item.Value?.Preview == "ToolCallLimit");
    }

    private static WorkflowNode UseHarness(WorkflowDocument graph)
    {
        var index = graph.Nodes.FindIndex(node => node.TypeId == NodeTypes.Agent);
        return graph.Nodes[index] = graph.Nodes[index] with { TypeId = NodeTypes.HarnessAgent };
    }

    private static void AddHarnessResource(WorkflowDocument graph, string harnessId, string id, string type, string port, NodeConfig config)
    {
        graph.Nodes.Add(new(id, type, 1, config));
        graph.Edges.Add(new(id, id, "resource", harnessId, port, "resource"));
    }

    [Fact]
    [Trait("Category", "Browser")]
    [Trait("Category", "LiveModel")]
    public async Task HarnessCollaboration_RealModelUsesTheReadOnlyMcpToolFromTheCanvas()
    {
        const string profileId = "phase07-harness-resource-live";
        using var api = Client();
        await SaveAsync(api, new(profileId, RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
            RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
            TimeoutSeconds: 90, Temperature: 0, MaxTokens: 1024));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1720, Height = 1000 } });
        await EditorTests.OpenAsync(page);
        await EditorTests.LoadExampleAsync(page, "harness-collaboration");
        await page.Locator("[data-node=model] [data-node-model]").SelectOptionAsync(profileId);
        await page.GetByRole(AriaRole.Button, new() { Name = "适应画布", Exact = true }).ClickAsync();
        await page.Locator("[data-node=harness] [data-node-help]").ClickAsync();
        await Expect(page.Locator("#node-help-dialog [data-node-help-maf]")).ToContainTextAsync("HarnessAgent");
        await page.Keyboard.PressAsync("Escape");
        await page.Locator("[data-run-start]").ClickAsync();
        var idLabel = page.Locator("[data-run-id]");
        await Expect(idLabel).ToBeVisibleAsync();
        var id = Guid.Parse(await idLabel.InnerTextAsync());
        var run = await WaitAsync(api, id, Terminal, 200);
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        await WorkflowTests.EvidenceAsync("phase07-harness-real-qwen.json", new { run, events });
        Assert.True(run.State == "Succeeded", $"{run.State} ({run.Error})");
        Assert.Contains("pump-01", run.Output);
        AssertHarnessCollaboration(events);
        await Expect(page.Locator("[data-node=output] [data-run-output]")).ToContainTextAsync("pump-01");
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase07-harness-qwen-canvas.png") });
    }

    private static void AssertHarnessCollaboration(RunEventItem[] events)
    {
        var requests = events.Where(item => item.Kind == "ModelRequest").ToArray();
        Assert.Equal(new[] { "triage", "harness" }, requests.Select(item => item.NodeId));
        using var chat = JsonDocument.Parse(requests[0].Value!.Preview);
        using var harness = JsonDocument.Parse(requests[1].Value!.Preview);
        Assert.Equal("MAF.AIAgentBinding", chat.RootElement.GetProperty("executor").GetString());
        Assert.Equal("MAF.AIAgentBinding", harness.RootElement.GetProperty("executor").GetString());
        Assert.Equal("ChatClientAgent", chat.RootElement.GetProperty("agentType").GetString());
        Assert.Equal("HarnessAgent", harness.RootElement.GetProperty("agentType").GetString());
        Assert.Contains(events, item => item.Kind == "BatchCompleted" &&
            item.Value?.Preview.Contains("HarnessAgent_agent_harness_", StringComparison.Ordinal) == true);
        var delegated = Assert.Single(events, item => item is { Kind: "NodeCompleted", NodeId: "triage" }).Value;
        Assert.Equal(delegated, Assert.Single(events, item => item is { Kind: "NodeStarted", NodeId: "harness" }).Value);
        Assert.Single(events, item => item is { Kind: "ToolRequest", NodeId: "harness", Text: "devices/read_device" });
        var result = Assert.Single(events, item => item is { Kind: "ToolResult", NodeId: "harness", Text: "devices/read_device" });
        Assert.Contains("warning", result.Value!.Preview);
    }
}
