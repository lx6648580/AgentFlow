using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

[Trait("Category", "RunHost")]
public partial class ModelRunTests
{
    private static string Host => Environment.GetEnvironmentVariable("AGENTFLOW_RUN_HOST") ?? "http://127.0.0.1:5188/";
    private static readonly ModelProfile Mock = new("mock", "", "mock");

    [Fact]
    public async Task Run_StoresImmutableDefinitionAndCancelsPendingJob()
    {
        using var api = Client();
        var prompt = new string('a', 920);
        var request = new RunRequest(Graph(prompt), Guid.NewGuid());
        var id = await StartAsync(api, request);
        try
        {
            await WaitAsync(api, id, run => run.State == "Running");
            var pending = await StartAsync(api, new(Graph("queued input"), Guid.NewGuid()));
            Assert.Equal("Queued", (await GetAsync<RunSnapshot>(api, $"api/runs/{pending}")).State);
            using var cancelled = await api.PostAsync($"api/runs/{pending}/cancel", null);
            Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<RunSnapshot>())!.State);
            await SaveAsync(api, Mock with { Streaming = false });
            request.Workflow.Nodes[0] = request.Workflow.Nodes[0] with { Config = new("edited after submission") };
            Assert.Equal(id, await StartAsync(api, request));
            var definition = await GetAsync<RunDefinition>(api, $"api/runs/{id}/definition");
            Assert.Equal(prompt, definition.Workflow.Nodes[0].Config.Text);
            Assert.True(Assert.IsType<ModelProfile>(definition.ModelProfile).Streaming);
            var result = await WaitAsync(api, id, Terminal);
            Assert.Equal("Succeeded", result.State);
            Assert.Equal("Mock: " + prompt, result.Output);
            Assert.Equal(prompt.Length, result.Usage?.InputTokens);
            var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
            Assert.Equal(new[] { "input", "agent", "output" },
                events.Where(item => item.Kind == "NodeStarted").Select(item => item.NodeId));
            Assert.Contains(events, item => item.Kind == "OutputDelta");
            var native = Assert.Single(events, item => item is { Kind: "ModelRequest", NodeId: "agent" });
            var requestBody = native.Value!.ArtifactId is { } artifact
                ? await api.GetStringAsync($"api/runs/{id}/artifacts/{artifact}")
                : native.Value.Preview;
            using var payload = JsonDocument.Parse(requestBody);
            Assert.Equal("MAF.AIAgentBinding", payload.RootElement.GetProperty("executor").GetString());
            Assert.Contains(events, item => item.Kind == "BatchCompleted" &&
                item.Value?.Preview.Contains("Agent_agent_agent_", StringComparison.Ordinal) == true);
            Assert.Equal(events.Select(item => item.Sequence).Order(), events.Select(item => item.Sequence));

            var nonStreaming = await StartAsync(api, new(Graph("buffered"), Guid.NewGuid()));
            var bufferedResult = await WaitAsync(api, nonStreaming, Terminal);
            Assert.Equal("Mock: buffered", bufferedResult.Output);
            Assert.Equal("Succeeded", bufferedResult.State);
            Assert.DoesNotContain(await GetAsync<RunEventItem[]>(api, $"api/runs/{nonStreaming}/events"),
                item => item.Kind == "OutputDelta");
            using var probe = await api.PostAsync("api/models/mock/tools", null);
            Assert.Equal("Supported", (await probe.Content.ReadFromJsonAsync<ToolProbe>())!.Status);
        }
        finally { await SaveAsync(api, Mock); }
    }

    [Fact]
    public async Task Run_ClassifiesTimeoutCancellationUnreachableAndInvalidGraph()
    {
        using var api = Client();
        await SaveAsync(api, Mock with { TimeoutSeconds = 1 });
        try
        {
            var timeout = await StartAsync(api, new(Graph(new string('z', 850)), Guid.NewGuid()));
            var timedOut = await WaitAsync(api, timeout, Terminal);
            Assert.Equal("TimedOut", timedOut.State);
            Assert.Equal("ModelTimeout", timedOut.Error);
            var cancelled = await StartAsync(api, new(Graph(new string('z', 850)), Guid.NewGuid()));
            await WaitAsync(api, cancelled, run => run.State == "Running");
            using var cancellation = await api.PostAsync($"api/runs/{cancelled}/cancel", null);
            cancellation.EnsureSuccessStatusCode();
            Assert.Equal("Cancelled", (await WaitAsync(api, cancelled, Terminal)).State);
        }
        finally { await SaveAsync(api, Mock); }

        const string unreachable = "phase03-unreachable";
        await SaveAsync(api, new(unreachable, "http://127.0.0.1:0/v1", "unreachable", TimeoutSeconds: 3));
        var failed = await StartAsync(api, new(Graph("ping", unreachable), Guid.NewGuid()));
        var failure = await WaitAsync(api, failed, Terminal);
        Assert.Equal("Failed", failure.State);
        Assert.Equal("ModelUnavailable", failure.Error);

        var invalid = Graph("ping");
        invalid.Edges[1] = invalid.Edges[1] with { Kind = "message" };
        using var rejected = await api.PostAsJsonAsync("api/runs", new RunRequest(invalid, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("UnsupportedEdge", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ModelCredentials_StayInHostAndProviderErrorsDoNotEchoHeaders()
    {
        using var api = Client();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string profileId = "phase03-auth-check";
        const string fixtureKey = "phase03-credential-regression-871108";
        const string reference = "PHASE03_TEST";
        var profile = new ModelProfile(profileId, $"http://127.0.0.1:{port}/v1", "auth-check", reference, TimeoutSeconds: 10);
        await SaveAsync(api, profile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var received = RejectModelCallAsync(listener, deadline.Token);
        var id = await StartAsync(api, new(Graph("auth test", profileId), Guid.NewGuid()));
        Assert.Equal("Bearer " + fixtureKey, await received);
        var result = await WaitAsync(api, id, Terminal);
        Assert.Equal("Failed", result.State);
        Assert.Equal("ModelRejected", result.Error);
        foreach (var path in new[] { "api/models", $"api/runs/{id}", $"api/runs/{id}/definition", $"api/runs/{id}/events" })
            Assert.DoesNotContain(fixtureKey, await api.GetStringAsync(path));

        await SaveAsync(api, profile with { Id = "phase03-missing-key", SecretRef = "PHASE03_NEVER_CONFIGURED" });
        var missing = await StartAsync(api, new(Graph("test", "phase03-missing-key"), Guid.NewGuid()));
        Assert.Equal("MissingSecret", (await WaitAsync(api, missing, Terminal)).Error);
        using var rejected = await api.PostAsJsonAsync("api/models", profile with { Id = "deny-unapproved", BaseUrl = "http://192.0.2.1/v1" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("ModelEndpointNotAllowed", await rejected.Content.ReadAsStringAsync());
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "api/models/mock/tools");
        crossOrigin.Headers.Add("Origin", "http://unapproved.example");
        using var blocked = await api.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RunPanel_StreamsAndRecoversSameRunAfterDisconnectLanguageChangeAndReload()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.WebSocket += (_, socket) =>
        {
            if (socket.Url.Contains("/hubs/runs"))
                socket.FrameReceived += (_, frame) =>
                {
                    if (frame.Text?.Contains("RunChanged") == true)
                        notification.TrySetResult();
                };
        };
        await OpenChatAsync(page);
        var prompt = new string('x', 1152);
        var input = page.Locator("[data-node=input] textarea");
        await input.FillAsync(prompt);
        await input.BlurAsync();
        await page.Locator("[data-run-start]").ClickAsync();
        var online = page.Locator("[data-run-state=Running]");
        await Expect(online).ToBeVisibleAsync();
        var id = await page.Locator("[data-run-id]").InnerTextAsync();
        await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Expect(page.Locator("[data-run-output]")).ToContainTextAsync("Mock:");
        await context.SetOfflineAsync(true);
        try
        {
            await Expect(page.Locator("[data-run-error]")).ToContainTextAsync("Reconnecting to this run");
            await page.Locator("[data-language]").SelectOptionAsync("zh-Hans");
            await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", "zh-Hans");
            await Expect(page.Locator("[data-run-id]")).ToHaveTextAsync(id);
        }
        finally { await context.SetOfflineAsync(false); }
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToHaveTextAsync("已完成", new() { Timeout = 15000 });
        await Expect(page.Locator("[data-run-id]")).ToHaveTextAsync(id);
        await page.Locator("[data-node=output] [data-output-details]").ClickAsync();
        await Expect(page.Locator("dialog[open] [data-output-full]")).ToHaveTextAsync("Mock: " + prompt);
        await page.Keyboard.PressAsync("Escape");
        await page.ReloadAsync();
        await Expect(page.Locator("[data-run-id]")).ToHaveTextAsync(id);
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToHaveTextAsync("已完成");
        if (WorkflowTests.EvidenceDirectory is { } directory)
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "recovered-mock-run.png") });
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Running]")).ToBeVisibleAsync();
        await page.Locator("[data-run-cancel]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Cancelled]")).ToBeVisibleAsync();
        Assert.Empty(errors);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task ModelManager_UpdatesNodeChoicesAndPreservesReferencesAcrossReload()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        await EditorTests.OpenAsync(page);
        var graph = Graph("Testing model binding", "legacy-model");
        graph.Nodes.Add(new("other-model", NodeTypes.Model, 1, new("mock")));
        graph.Layout.Add("other-model", new(550, 370));
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "model-reference.workflow.json",
            MimeType = "application/json",
            Buffer = JsonSerializer.SerializeToUtf8Bytes(graph, WorkflowDocument.Json)
        });
        var first = page.Locator("[data-node=model] [data-node-model]");
        var second = page.Locator("[data-node=other-model] [data-node-model]");
        await Expect(first).ToHaveValueAsync("legacy-model");
        await Expect(first.Locator("option:checked")).ToHaveTextAsync("legacy-model · Not in model list");

        await page.Locator("[data-models-toggle]").ClickAsync();
        var manager = page.GetByRole(AriaRole.Dialog, new() { Name = "Resources" });
        await Expect(manager).ToBeVisibleAsync();
        var profile = "ui-model-" + Guid.NewGuid().ToString("N")[..8];
        await CreateAsync(profile, "ui-provider");
        await Expect(page.Locator("[data-model-feedback=ProfileSaved]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-model-id]")).ToHaveAttributeAsync("readonly", "");
        await CreateAsync(profile, "duplicate-provider");
        await Expect(page.Locator("[data-model-feedback=ModelProfileExists]")).ToBeVisibleAsync();
        await Expect(page.Locator($"[data-model-profile={profile}]")).ToContainTextAsync("ui-provider");
        await page.Locator($"[data-model-profile={profile}]").ClickAsync();
        await page.Locator("[data-model-provider]").FillAsync("edited-provider");
        await page.Locator("[data-model-save]").ClickAsync();
        await Expect(page.Locator($"[data-model-profile={profile}]")).ToContainTextAsync("edited-provider");
        await page.Keyboard.PressAsync("Escape");
        await Expect(manager).ToBeHiddenAsync();

        await first.SelectOptionAsync(profile);
        await Expect(first).ToHaveValueAsync(profile);
        await Expect(second).ToHaveValueAsync("mock");
        await Expect(page.Locator(".toolbar-run")).ToBeVisibleAsync();
        await Expect(page.Locator(".toolbar-run [data-model-save]")).ToHaveCountAsync(0);

        await page.RouteAsync("**/api/models", route => route.AbortAsync());
        await page.ReloadAsync();
        await Expect(first).ToHaveValueAsync(profile);
        await Expect(first.Locator("option:checked")).ToHaveTextAsync(profile + " · Model list unavailable");
        await Expect(second).ToHaveValueAsync("mock");
        await page.UnrouteAsync("**/api/models");
        await page.Locator("[data-models-toggle]").ClickAsync();
        await Expect(page.Locator($"[data-model-profile={profile}]")).ToBeVisibleAsync();
        await page.Locator("[data-model-close]").ClickAsync();
        await Expect(first.Locator("option:checked")).ToHaveTextAsync(profile);
        await Expect(first).ToHaveValueAsync(profile);
        await Expect(second).ToHaveValueAsync("mock");

        async Task CreateAsync(string id, string model)
        {
            await page.Locator("[data-model-new]").ClickAsync();
            await page.Locator("[data-model-id]").FillAsync(id);
            await page.Locator("[data-model-url]").FillAsync("http://127.0.0.1:0/v1");
            await page.Locator("[data-model-provider]").FillAsync(model);
            await page.Locator("[data-model-save]").ClickAsync();
        }
    }

    [Fact]
    [Trait("Category", "Browser")]
    [Trait("Category", "LiveModel")]
    public async Task RunPanel_LiveModelSelectedInNodeReturnsActualResponse()
    {
        var baseUrl = RequiredEnvironment("AGENTFLOW_LIVE_BASE_URL");
        var model = RequiredEnvironment("AGENTFLOW_LIVE_MODEL_ID");
        var reference = RequiredEnvironment("AGENTFLOW_LIVE_SECRET_REF");
        const string profileId = "qwen-live";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1520, Height = 960 } });
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await OpenChatAsync(page);
        await page.Locator("[data-models-toggle]").ClickAsync();
        await Expect(page.Locator("[data-model-new]")).ToBeEnabledAsync();
        var existing = page.Locator($"[data-model-profile={profileId}]");
        if (await existing.CountAsync() > 0)
            await existing.ClickAsync();
        else
        {
            await page.Locator("[data-model-new]").ClickAsync();
            await page.Locator("[data-model-id]").FillAsync(profileId);
        }
        await page.Locator("[data-model-url]").FillAsync(baseUrl);
        await page.Locator("[data-model-provider]").FillAsync(model);
        await page.Locator("[data-model-secret]").FillAsync(reference);
        await page.Locator("[data-model-timeout]").FillAsync("90");
        await page.Locator("[data-model-temperature]").FillAsync("0");
        await page.Locator("[data-model-tokens]").FillAsync("256");
        await page.Locator("[data-model-save]").ClickAsync();
        await Expect(page.Locator("[data-model-feedback=ProfileSaved]")).ToBeVisibleAsync();
        await page.Locator("[data-model-probe]").ClickAsync();
        var probe = page.Locator("[data-model-feedback^=Tools]");
        await Expect(probe).ToBeVisibleAsync(new() { Timeout = 95000 });
        await WorkflowTests.EvidenceAsync("live-tool-probe.json", new { status = await probe.GetAttributeAsync("data-model-feedback") });
        if (WorkflowTests.EvidenceDirectory is { } modelsEvidence)
            await page.ScreenshotAsync(new() { Path = Path.Combine(modelsEvidence, "live-model-catalog.png") });
        await page.Locator("[data-model-close]").ClickAsync();
        var choice = page.Locator("[data-node=model] [data-node-model]");
        await choice.SelectOptionAsync(profileId);
        await Expect(choice).ToHaveValueAsync(profileId);
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-id]")).ToBeVisibleAsync();
        var id = Guid.Parse(await page.Locator("[data-run-id]").InnerTextAsync());
        using var api = Client();
        var result = await WaitAsync(api, id, Terminal, seconds: 95);
        await WorkflowTests.EvidenceAsync("live-run.json", result);
        var events = await GetAsync<RunEventItem[]>(api, $"api/runs/{id}/events");
        await WorkflowTests.EvidenceAsync("live-events.json", events);
        await WorkflowTests.EvidenceAsync("live-definition.json", await GetAsync<RunDefinition>(api, $"api/runs/{id}/definition"));
        Assert.True(result.State == "Succeeded", $"Live workflow finished as {result.State} ({result.Error}).");
        Assert.Equal(profileId, result.ProfileId);
        Assert.Equal("READY", result.Output.Trim());
        Assert.Contains(events, item => item.Kind == "OutputDelta");
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-run-output]")).ToHaveTextAsync("READY");
        if (WorkflowTests.EvidenceDirectory is { } directory)
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "live-model-run.png") });
        Assert.Empty(errors);
    }

    private static WorkflowDocument Graph(string input, string profile = "mock")
    {
        var graph = WorkflowDocument.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "chat.workflow.json")));
        graph.Nodes[0] = graph.Nodes[0] with { Config = new(input) };
        graph.Nodes[1] = graph.Nodes[1] with { Config = new(profile) };
        return graph;
    }

    private static async Task OpenChatAsync(IPage page)
    {
        await EditorTests.OpenAsync(page);
        await EditorTests.LoadExampleAsync(page, "chat");
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("5");
    }

    private static HttpClient Client() => new() { BaseAddress = new(Host), Timeout = TimeSpan.FromSeconds(8) };

    private static async Task SaveAsync(HttpClient api, ModelProfile profile)
    {
        using var response = await api.PostAsJsonAsync("api/models", profile);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> StartAsync(HttpClient api, RunRequest request)
    {
        using var response = await api.PostAsJsonAsync("api/runs", request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RunStarted>())!.RunId;
    }

    private static async Task<T> GetAsync<T>(HttpClient api, string path) =>
        (await api.GetFromJsonAsync<T>(path))!;

    private static async Task<RunSnapshot> WaitAsync(HttpClient api, Guid id, Func<RunSnapshot, bool> condition, int seconds = 15)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var snapshot = (await api.GetFromJsonAsync<RunSnapshot>($"api/runs/{id}", deadline.Token))!;
            if (condition(snapshot))
                return snapshot;
            Assert.False(Terminal(snapshot), $"Unexpected terminal state {snapshot.State} ({snapshot.Error}).");
            await Task.Delay(80, deadline.Token);
        }
    }

    private static bool Terminal(RunSnapshot run) => run.State is "Succeeded" or "Failed" or "Cancelled" or "TimedOut";
    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} to run the live model test.");

    private static async Task<string> RejectModelCallAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        string authorization = "";
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                authorization = line["Authorization:".Length..].Trim();
        }
        var body = JsonSerializer.Serialize(new { error = new { message = authorization, type = "test_auth" } });
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        return authorization;
    }
}
