using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    [Fact]
    [Trait("Category", "RunHost")]
    public async Task OutputNodes_RecordIndependentResultsAndReuseTheDiagnosticArtifact()
    {
        using var api = Client();
        var graph = Example("phase07/outputs");
        var payload = JsonSerializer.Serialize(new { status = "ready", description = new string('x', 1300) });
        SetText(graph, "input", payload);
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Succeeded", run.State);
        Assert.Empty(run.Output);
        var values = Assert.IsType<RunOutput[]>(run.Outputs);
        Assert.Equal(new[] { "output-json", "output-summary" }, values.Select(output => output.NodeId));
        Assert.Equal("application/json", values[0].ContentType);
        Assert.Equal("text/plain", values[1].ContentType);
        Assert.Equal("Release 42 is ready.", values[1].Value.Preview);
        var artifact = Assert.IsType<Guid>(values[0].Value.ArtifactId);
        Assert.Equal(payload, await api.GetStringAsync($"api/runs/{run.RunId}/artifacts/{artifact}"));
        Assert.Equal(values[0].Value, Assert.Single(events, item => item is { Kind: "NodeCompleted", NodeId: "output-json" }).Value);
        var recorded = await GetAsync<RunDiagnosticReport>(api, $"api/runs/{run.RunId}/report");
        Assert.Equal(values, recorded.Recording.Snapshot.Outputs);
        Assert.Equal(payload, recorded.Artifacts[artifact]);
        await WorkflowTests.EvidenceAsync("phase07-output-references.json", recorded);
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task OutputNodes_ConditionProducesOnlyTheReachedLeaf()
    {
        using var api = Client();
        var basis = Example("phase07/outputs");
        var graph = basis with
        {
            Nodes = [basis.Nodes[0], new("condition", NodeTypes.Condition, 1, new("/release >= 42")), .. basis.Nodes.Skip(2)],
            Edges =
            [
                new("condition", "input", "text", "condition", "in", "message"),
                new("true", "condition", "yes", "output-json", "value", "message"),
                new("false", "condition", "no", "output-summary", "value", "message")
            ]
        };
        var (run, events) = await ExecuteAdvancedAsync(api, graph);
        Assert.Equal("Succeeded", run.State);
        Assert.Empty(run.Output);
        Assert.Equal("output-json", Assert.Single(run.Outputs!).NodeId);
        Assert.Contains(events, item => item is { Kind: "NodeSkipped", NodeId: "output-summary" });
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task OutputNodes_CanvasOwnsResultsAndTheDockShowsReferencesAndInspection()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1540, Height = 980 } });
        await EditorTests.OpenAsync(page);
        await EditorTests.LoadExampleAsync(page, "outputs");
        var json = page.Locator("[data-node=output-json]");
        var summary = page.Locator("[data-node=output-summary]");
        await Expect(json).ToBeVisibleAsync();
        await page.Locator("[data-kind='editor.run-terminal'] [data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(summary.Locator("[data-run-output]")).ToHaveTextAsync("Release 42 is ready.");
        await Expect(json.Locator("[data-run-output]")).ToHaveTextAsync("{\"status\":\"ready\",\"release\":42}");
        await Expect(json).ToHaveAttributeAsync("data-node-state", "Completed");
        await Expect(page.Locator("#run-diagnostics [data-run-output]")).ToHaveCountAsync(0);

        await json.Locator("[data-output-details]").ClickAsync();
        await Expect(page.Locator("dialog[open] [data-output-full]")).ToContainTextAsync("\"release\":42");
        var downloaded = await page.RunAndWaitForDownloadAsync(() => page.Locator("dialog[open] [data-output-download]").ClickAsync());
        Assert.Equal("output-output-json.json", downloaded.SuggestedFilename);
        using (var result = JsonDocument.Parse(await File.ReadAllTextAsync(await downloaded.PathAsync())))
            Assert.Equal(42, result.RootElement.GetProperty("release").GetInt32());
        await page.Keyboard.PressAsync("Escape");
        await page.Locator("[data-run-queue]").ClickAsync();
        await Expect(page.Locator("[data-trace-tab=queue]")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.Locator("[data-trace-event-kind=NodeCompleted][data-trace-node=output-json]").ClickAsync();
        await Expect(page.Locator("[data-console-detail=NodeCompleted]")).ToContainTextAsync("ms");
        await Expect(json).ToHaveAttributeAsync("data-inspected", "true");
        await page.Locator("[data-trace-event-kind=NodeStarted][data-trace-node=output-json]").ClickAsync();
        await Expect(page.Locator("[data-console-detail=NodeStarted] [data-trace-value=NodeStarted]")).ToContainTextAsync("ready");
        if (WorkflowTests.EvidenceDirectory is { } directory)
            await page.ScreenshotAsync(new() { Path = Path.Combine(directory, "phase07-output-canvas.png") });
        var runId = await page.Locator("[data-run-id]").InnerTextAsync();
        await page.Locator("[data-trace-tab=history]").ClickAsync();
        await page.Locator($"[data-history-run='{runId}']").ClickAsync();
        await Expect(json.Locator("[data-run-output]")).ToContainTextAsync("ready");
        await json.Locator("[data-output-details]").ClickAsync();
        await Expect(page.Locator("dialog[open] .output-meta")).ToContainTextAsync("JSON");
    }
}
