using System.Net.Http.Json;
using AgentFlow.Editor.POC;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

public partial class ModelRunTests
{
    [Fact]
    public void WorkflowExamples_CoverEveryBuiltInAndBundledPluginNode()
    {
        var catalog = NodeSdkTests.Samples().CreateCatalog();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(WorkflowExamples.All.Length, WorkflowExamples.All.DistinctBy(example => example.Id).Count());
        foreach (var example in WorkflowExamples.All)
        {
            var graph = ReadExample(example, catalog);
            covered.UnionWith(WorkflowExamples.Nodes(graph).Select(node => node.TypeId));
            if (!example.EditorOnly)
                covered.Add(NodeTypes.RunTerminal);
            Assert.Contains(example.Title, UiText.Keys);
            Assert.Contains(example.Group, UiText.Keys);
            Assert.All(new[] { example.Guide.En, example.Guide.Hans, example.Guide.Hant }, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.All(graph.Nodes, node => Assert.True(graph.Layout.ContainsKey(node.Id), example.Id));
        }
        Assert.Equal(catalog.Latest.Select(node => node.TypeId).Order(), covered.Order());
    }

    [Fact]
    [Trait("Category", "RunHost")]
    public async Task WorkflowExamples_EveryRunnableExampleProducesAnOutput()
    {
        using var api = Client();
        var catalog = NodeSdkTests.Samples().CreateCatalog();
        foreach (var example in WorkflowExamples.All.Where(example => !example.EditorOnly))
        {
            var graph = ReadExample(example, catalog);
            graph.Nodes.Add(new("example-terminal", NodeTypes.RunTerminal, 1, new()));
            var human = example.Id is "approval" or "input";
            var id = await StartAsync(api, new(graph, Guid.NewGuid(), human ? null : new() { Interactive = true }));
            try
            {
                if (human)
                {
                    var waiting = await WaitAsync(api, id, run => run.State == "Waiting" || Terminal(run));
                    Assert.True(waiting.Waiting is not null, $"{example.Id}: {waiting.State} ({waiting.Error})");
                    var decision = example.Id == "approval" ? new HumanDecision(waiting.Waiting.RequestId, "operator", "approve")
                        : new HumanDecision(waiting.Waiting.RequestId, "operator", "submit", "Example release note");
                    using var response = await api.PostAsJsonAsync($"api/runs/{id}/decision", decision);
                    response.EnsureSuccessStatusCode();
                }
                var final = await WaitAsync(api, id, Terminal);
                Assert.True(final.State == "Succeeded", $"{example.Id}: {final.State} ({final.Error})");
                Assert.NotEmpty(final.Outputs!);
                if (example.Id == "switch-routing")
                    Assert.Equal("urgent", Assert.Single(final.Outputs!).NodeId);
                if (example.Id == "sdk-concat")
                    Assert.Equal("READY / RELEASE", final.Output);
                if (example.Id == "sdk-json")
                    Assert.Equal("{\"state\":\"ready\"}", final.Output);
            }
            finally
            {
                var latest = await GetAsync<RunSnapshot>(api, $"api/runs/{id}");
                if (!Terminal(latest))
                {
                    using var response = await api.PostAsync($"api/runs/{id}/cancel", null);
                    response.EnsureSuccessStatusCode();
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task WorkflowExamples_DialogShowsNodeGuidesAndLoadsIntoANewTab()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1550, Height = 960 } });
        await EditorTests.OpenAsync(page);
        await page.Locator("[data-library='core.text-input']").ClickAsync();
        var draft = page.Locator("[data-kind='core.text-input'] textarea");
        await draft.FillAsync("my draft");
        var open = page.Locator("[data-open-examples]");
        await open.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "工作流示例" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator("[data-example]")).ToHaveCountAsync(WorkflowExamples.All.Length);
        await Expect(page.Locator(".workflow-tab")).ToHaveCountAsync(1);
        await page.Keyboard.PressAsync("Escape");
        await Expect(draft).ToHaveValueAsync("my draft");
        await open.ClickAsync();
        await dialog.GetByRole(AriaRole.Searchbox).FillAsync("flow.switch");
        await Expect(dialog.Locator("[data-example]")).ToHaveCountAsync(1);
        await Expect(dialog.Locator("[data-example-guide]")).ToContainTextAsync("优先级");
        await Expect(dialog.Locator("[data-example-node='flow.switch']")).ToHaveTextAsync("多路选择");
        if (WorkflowTests.EvidenceDirectory is { } output)
        {
            await dialog.GetByRole(AriaRole.Searchbox).ClearAsync();
            await dialog.Locator("[data-example=switch-routing]").ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "workflow-examples.png") });
        }
        await dialog.Locator("[data-load-example]").ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.Locator(".workflow-tab")).ToHaveCountAsync(2);
        await Expect(page.Locator("[data-kind='flow.switch']")).ToBeVisibleAsync();
        await page.Locator("[data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-node=urgent] [data-run-output]")).ToContainTextAsync("Inspect pump-01");
        await page.Locator(".workflow-tab .tab-caption").First.ClickAsync();
        await Expect(draft).ToHaveValueAsync("my draft");
        await EditorTests.LoadExampleAsync(page, "editor-ports");
        await Expect(page.Locator("[data-kind='poc.port-probe']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-run-start]")).ToBeDisabledAsync();
    }

    private static WorkflowDocument ReadExample(WorkflowExample example, NodeCatalog catalog)
    {
        using var reader = new StreamReader(typeof(WorkflowExamples).Assembly.GetManifestResourceStream(example.Resource)!);
        return WorkflowDocument.Read(reader.ReadToEnd(), catalog);
    }
}
