using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    private static string DeviceSampleUrl => Environment.GetEnvironmentVariable("AGENTFLOW_DEVICE_SAMPLE_URL") ?? "http://127.0.0.1:5190/";

    [Fact]
    public async Task Integration_RunReadsSimulatedHttpRealHttpFileSqliteAndMcp()
    {
        using var api = Client();
        var catalog = await GetAsync<ToolCatalogInfo>(api, "api/tools");
        Assert.Contains("device-sim", catalog.Http);
        Assert.Contains("device-api", catalog.Http);
        Assert.Contains("device-history", catalog.Sqlite);
        Assert.Contains("devices", catalog.Mcp);
        var listed = await GetAsync<McpToolInfo[]>(api, "api/tools/mcp/devices");
        var read = Assert.Single(listed);
        Assert.Equal("read_device", read.Name);
        Assert.Equal("string", read.InputSchema.GetProperty("properties").GetProperty("deviceId").GetProperty("type").GetString());

        var simulation = Example("device-diagnosis");
        SetToolParameters(simulation, "status", new { connection = "device-sim" });
        var (sim, simEvents) = await ExecuteAdvancedAsync(api, simulation);
        var (diagnosis, diagnosisEvents) = await ExecuteAdvancedAsync(api, Example("device-diagnosis"));
        Assert.Equal("Mock: warning", sim.Output);
        Assert.Equal("Mock: warning", diagnosis.Output);
        Assert.Contains(diagnosisEvents, item => item is { Kind: "ToolResult", NodeId: "status", Text: "device-api" });
        var (file, fileEvents) = await ExecuteAdvancedAsync(api, Example("device-file"));
        Assert.Equal("warning", file.Output);
        Assert.Contains(fileEvents, item => item is { Kind: "ToolResult", Text: "device-status.json" });
        var (database, databaseEvents) = await ExecuteAdvancedAsync(api, Example("device-sqlite"));
        using var rows = JsonDocument.Parse(database.Output);
        Assert.Equal(72.5, rows.RootElement[0].GetProperty("temperature").GetDouble());
        Assert.Equal("pump-01", rows.RootElement[0].GetProperty("device_id").GetString());
        var (mcp, mcpEvents) = await ExecuteAdvancedAsync(api, Example("device-mcp"));
        using var device = JsonDocument.Parse(mcp.Output);
        Assert.Equal("warning", device.RootElement.GetProperty("state").GetString());
        Assert.Contains(mcpEvents, item => item is { Kind: "ToolResult", NodeId: "status", Text: "devices/read_device" });
        await WorkflowTests.EvidenceAsync("phase06-integrations.json",
            new { listed, sim, simEvents, diagnosis, diagnosisEvents, file, fileEvents, database, databaseEvents, mcp, mcpEvents });
    }

    [Fact]
    public async Task ToolPermissions_BlockTraversalSqlSyntaxRedirectAndUnapprovedConnections()
    {
        using var api = Client();
        foreach (var path in new[] { "../model-profiles.json", "C:\\Windows\\win.ini", "device-status.json:other", "folder/../data.json" })
        {
            var graph = Example("device-file");
            SetToolParameters(graph, "status", new { path });
            await AssertRejectedAsync(api, graph, "ToolPathDenied");
        }

        var sqlite = Example("device-sqlite");
        SetToolParameters(sqlite, "status", new { source = "device-history", columns = new[] { "state; DELETE FROM devices" } });
        await AssertRejectedAsync(api, sqlite, "InvalidSqliteQuery");
        SetToolParameters(sqlite, "status", new { source = "device-history", columns = new[] { "state" }, equals = new { device_id = "' OR 1=1 --" } });
        var (noRows, _) = await ExecuteAdvancedAsync(api, sqlite);
        Assert.Equal("[]", noRows.Output);

        foreach (var (alias, expected) in new[]
        {
            ("unknown-connection", "ToolAccessDenied"), ("sim-redirect", "ToolRedirectDenied"),
            ("sim-binary", "ToolContentType"), ("sim-large", "ToolOutputLimit")
        })
        {
            var graph = Example("device-diagnosis");
            SetToolParameters(graph, "status", new { connection = alias });
            var (run, events) = await ExecuteAdvancedAsync(api, graph);
            Assert.Equal(expected, run.Error);
            Assert.Contains(events, item => item.Kind == "ToolRejected" && item.Value?.Preview == expected);
            Assert.DoesNotContain("agent", Started(events));
        }

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "api/tools/mcp/devices");
        crossOrigin.Headers.Add("Origin", "http://unapproved.example");
        using var response = await api.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task McpReadApproval_RejectsWritesInvalidArgumentsAndRemoteErrors()
    {
        using var api = Client();
        using var device = new HttpClient { BaseAddress = new(DeviceSampleUrl) };
        var before = await GetAsync<JsonElement>(device, "sim/counts");
        var graph = Example("device-mcp");
        SetToolParameters(graph, "status", new { server = "devices", tool = "write_device", arguments = new { deviceId = "pump-01" } });
        var (write, writeEvents) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("ToolAccessDenied", write.Error);
        Assert.Contains(writeEvents, item => item is { Kind: "ToolRejected", Text: "devices/write_device" });

        SetToolParameters(graph, "status", new { server = "devices", tool = "read_device", arguments = new { deviceId = 7 } });
        var (invalid, _) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("InvalidToolArguments", invalid.Error);
        var after = await GetAsync<JsonElement>(device, "sim/counts");
        Assert.Equal(before.GetProperty("readCalls").GetInt32(), after.GetProperty("readCalls").GetInt32());
        Assert.Equal(0, after.GetProperty("writeCalls").GetInt32());

        SetToolParameters(graph, "status", new { server = "devices", tool = "read_device", arguments = new { deviceId = "does-not-exist" } });
        var (missing, _) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("ToolRemoteError", missing.Error);
        await WorkflowTests.EvidenceAsync("phase06-mcp-denied.json", new { before, write, writeEvents, invalid, after, missing });
    }

    [Fact]
    public async Task ToolRequest_CanTimeoutAndCancelBeforeDownstreamAgent()
    {
        using var api = Client();
        var graph = Example("device-diagnosis");
        SetToolParameters(graph, "status", new { connection = "sim-slow", seconds = 1 });
        var (timeout, timeoutEvents) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("ToolTimeout", timeout.Error);
        Assert.DoesNotContain("agent", Started(timeoutEvents));
        SetToolParameters(graph, "status", new { connection = "sim-slow", seconds = 20 });
        var id = await StartAsync(api, new(graph, Guid.NewGuid()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!(await api.GetFromJsonAsync<RunEventItem[]>($"api/runs/{id}/events", deadline.Token))!
                   .Any(item => item.Kind == "ToolRequest"))
            await Task.Delay(60, deadline.Token);
        using var cancel = await api.PostAsync($"api/runs/{id}/cancel", null, deadline.Token);
        cancel.EnsureSuccessStatusCode();
        var final = await WaitAsync(api, id, Terminal, seconds: 5);
        Assert.Equal("Cancelled", final.State);
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        Assert.DoesNotContain("agent", Started(events));
        await WorkflowTests.EvidenceAsync("phase06-timeout-and-cancel.json", new { timeout, timeoutEvents, final, events });
    }

    [Fact]
    public async Task AgentToolBinding_CallsTheApprovedToolThroughTheModel()
    {
        using var api = Client();
        var (run, events) = await ExecuteAdvancedAsync(api, Example("device-agent-tool"));
        Assert.Equal("Succeeded", run.State);
        Assert.Contains("pump-01", run.Output);
        Assert.Contains("72.5", run.Output);
        Assert.Single(events, item => item is { Kind: "ToolRequest", NodeId: "agent", Text: "devices/read_device" });
        Assert.Single(events, item => item is { Kind: "ToolResult", NodeId: "agent", Text: "devices/read_device" });
        await WorkflowTests.EvidenceAsync("phase06-mock-agent-tool.json", new { run, events });
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task McpNode_SelectsARegisteredToolAndRunsWithWorkflowArguments()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1530, Height = 980 } });
        await EditorTests.OpenAsync(page);
        await OpenToolSampleAsync(page, "device-mcp");
        var tool = page.Locator("[data-mcp-reference]");
        await Expect(tool.Locator("option[value='devices/read_device']")).ToHaveCountAsync(1);
        await Expect(tool.Locator("option[value='devices/write_device']")).ToHaveCountAsync(0);
        await tool.SelectOptionAsync("devices/read_device");
        var device = page.Locator("[data-parameter=arguments]");
        await device.FillAsync("""{"deviceId":"pump-02"}""");
        await device.BlurAsync();
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-run-output]")).ToContainTextAsync("pump-02");
        await Expect(page.Locator("[data-run-output]")).ToContainTextAsync("normal");
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase06-mcp-editor.png") });
    }

    [Fact]
    [Trait("Category", "Browser")]
    [Trait("Category", "LiveModel")]
    public async Task DeviceAgent_UsesLiveModelToCallTheControlledMcpServer()
    {
        const string profileId = "qwen-live";
        using var api = Client();
        await SaveAsync(api, new(profileId, RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL"),
            RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID"), RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF"),
            TimeoutSeconds: 90, Temperature: 0, MaxTokens: 256));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1530, Height = 980 } });
        await EditorTests.OpenAsync(page);
        await OpenToolSampleAsync(page, "device-agent-tool");
        await page.Locator("[data-node=model] [data-node-model]").SelectOptionAsync(profileId);
        await page.Locator("[data-run-start]").ClickAsync();
        var idLabel = page.Locator("[data-run-id]");
        await Expect(idLabel).ToBeVisibleAsync();
        var id = Guid.Parse(await idLabel.InnerTextAsync());
        var run = await WaitAsync(api, id, Terminal, 100);
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        await WorkflowTests.EvidenceAsync("phase06-live-agent-tool.json", new { run, events });
        Assert.True(run.State == "Succeeded", $"{run.State} ({run.Error})");
        Assert.False(string.IsNullOrWhiteSpace(run.Output));
        Assert.Contains(events, item => item is { Kind: "ToolResult", NodeId: "agent", Text: "devices/read_device" } &&
            item.Value?.Preview.Contains("warning", StringComparison.Ordinal) == true);
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase06-live-agent-editor.png") });
    }

    private static async Task OpenToolSampleAsync(IPage page, string name)
    {
        await EditorTests.LoadExampleAsync(page, name);
        await Expect(page.Locator("[data-node=output]")).ToBeVisibleAsync();
    }

    private static void SetToolParameters<T>(WorkflowDocument graph, string id, T value)
    {
        var index = graph.Nodes.FindIndex(node => node.Id == id);
        graph.Nodes[index] = graph.Nodes[index] with { Config = Parameters(value) };
    }
}
