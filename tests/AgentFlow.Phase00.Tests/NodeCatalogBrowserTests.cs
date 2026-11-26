using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

[Trait("Category", "Browser")]
public class NodeCatalogBrowserTests
{
    private static string Api => Environment.GetEnvironmentVariable("AGENTFLOW_NODE_CATALOG_API") ?? "http://127.0.0.1:5188/api/nodes";

    [Fact]
    public async Task CanvasHelp_ExplainsTheCurrentMafBindingAndPluginContract()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1450, Height = 900 } });
        await EditorTests.OpenAsync(page);
        await EditorTests.LoadExampleAsync(page, "chat");
        var agent = page.Locator("[data-kind='ai.agent']");
        await agent.Locator("[data-node-help]").ClickAsync();
        var help = page.Locator("#node-help-dialog");
        await Expect(help).ToBeVisibleAsync();
        await Expect(help.Locator("#node-help-title")).ToContainTextAsync("聊天客户端智能体");
        await Expect(help.Locator("[data-node-help-usage]")).ToContainTextAsync("聊天模型");
        await Expect(help.Locator("[data-node-help-maf]")).ToContainTextAsync("ChatClientAgent");
        await Expect(help.Locator("[data-node-help-port=model]")).ToBeVisibleAsync();
        await Expect(help.Locator("[data-node-help-parameter=tools]")).ToBeVisibleAsync();
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase07-node-help.png") });
        await page.Keyboard.PressAsync("Escape");
        await Expect(help).ToBeHiddenAsync();

        await page.Locator("[data-language]").SelectOptionAsync("en");
        await agent.Locator("header button").Last.ClickAsync();
        await agent.Locator("[data-node-help]").PressAsync("Enter");
        await Expect(help.Locator("#node-help-title")).ToContainTextAsync("ChatClientAgent");
        await Expect(help.GetByRole(AriaRole.Heading, new() { Name = "Relationship to MAF" })).ToBeVisibleAsync();
        await help.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "New workflow", Exact = true }).ClickAsync();
        await page.Locator("[data-library='sample.concat']").DragToAsync(page.Locator(".canvas-host"),
            new() { TargetPosition = new() { X = 250, Y = 220 } });
        var plugin = page.Locator("[data-kind='sample.concat']");
        await plugin.Locator("[data-node-help]").ClickAsync();
        await Expect(help.Locator("[data-node-help-usage]")).ToContainTextAsync("separator");
        await Expect(help.Locator("[data-node-help-maf]")).ToContainTextAsync("MAF Workflow Executor");
        await Expect(help.Locator("[data-node-help-parameter=repeat]")).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await page.Locator("[data-language]").SelectOptionAsync("zh-Hant");
        await plugin.Locator("[data-node-help]").ClickAsync();
        await Expect(help.Locator("[data-node-help-usage]")).ToContainTextAsync("分隔符號");
    }

    [Fact]
    public async Task RegisteredDll_ExecutesInMafFromImportedCanvas()
    {
        var graph = new WorkflowDocument("0.1", "sdk-runtime", 1,
            [new("input", NodeTypes.Input, 1, new("ready")),
             new("prompt", NodeTypes.Prompt, 1, new("release")),
             new("concat", "sample.concat", 1, new(Parameters: new()
             {
                 ["separator"] = JsonSerializer.SerializeToElement(" / "),
                 ["casing"] = JsonSerializer.SerializeToElement("upper")
             })),
             new("output", NodeTypes.Output, 1, new())],
            [new("left", "input", "text", "concat", "left", "message"),
             new("branch", "input", "text", "prompt", "in", "message"),
             new("right", "prompt", "text", "concat", "right", "message"),
             new("result", "concat", "text", "output", "value", "message")],
            new() { ["input"] = new(60, 100), ["prompt"] = new(310, 385), ["concat"] = new(570, 155), ["output"] = new(890, 200) });

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "zh-CN", ViewportSize = new() { Width = 1580, Height = 930 } });
        await EditorTests.OpenAsync(page);
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "sdk-runtime.workflow.json", MimeType = "application/json",
            Buffer = JsonSerializer.SerializeToUtf8Bytes(graph, WorkflowDocument.Json)
        });
        await Expect(page.Locator("[data-node=concat]")).ToBeVisibleAsync();
        await page.Locator(".studio-bar [data-run-start]").ClickAsync();
        await Expect(page.Locator("[data-run-state=Succeeded]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-node=output] [data-run-output]")).ToHaveTextAsync("READY / RELEASE");

        var runId = Guid.Parse(await page.Locator("[data-run-id]").InnerTextAsync());
        using var api = new HttpClient();
        var recording = (await api.GetFromJsonAsync<RunRecording>(new Uri(new Uri(Api), $"runs/{runId}/recording")))!;
        var input = Assert.Single(recording.Events, item => item is { Kind: "NodeStarted", NodeId: "concat" });
        using var inputs = JsonDocument.Parse(input.Value!.Preview);
        Assert.Equal("ready", inputs.RootElement.GetProperty("left").GetString());
        Assert.Equal("release", inputs.RootElement.GetProperty("right").GetString());
        Assert.Contains(recording.Events, item => item is { Kind: "BatchCompleted" } &&
            item.Value?.Preview.Contains("\"concat\"", StringComparison.Ordinal) == true);
        Assert.Equal("READY / RELEASE", Assert.Single(recording.Snapshot.Outputs!).Value.Preview);
        await WorkflowTests.EvidenceAsync("phase07-sdk-runtime.json", recording);
        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "phase07-sdk-runtime-canvas.png") });
    }

    [Fact]
    public async Task RegisteredDll_PopulatesLocalizedNodesAndRoundTripsTypedParameters()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        await EditorTests.OpenAsync(page);
        var concatButton = page.Locator("[data-library='sample.concat']");
        await Expect(concatButton).ToHaveTextAsync("Concatenate text");
        await Expect(concatButton).ToHaveAttributeAsync("title", "Joins two text inputs and repeats the result.");
        await page.GetByRole(AriaRole.Button, new() { Name = "New workflow", Exact = true }).ClickAsync();
        var canvas = page.Locator(".canvas-host");
        await concatButton.DragToAsync(canvas, new() { TargetPosition = new() { X = 220, Y = 145 } });
        await page.Locator("[data-library='sample.json-pick']").DragToAsync(canvas,
            new() { TargetPosition = new() { X = 600, Y = 145 } });
        var concat = page.Locator("[data-kind='sample.concat']");
        var picker = page.Locator("[data-kind='sample.json-pick']");
        await Expect(concat.Locator("[data-port=left]")).ToContainTextAsync("Left text");
        await Expect(concat.Locator("[data-port=right]")).ToHaveCountAsync(1);
        await Expect(picker.Locator("[data-port=json]")).ToContainTextAsync("JSON input");
        await Expect(concat.Locator("[data-parameter=repeat]")).ToHaveValueAsync("1");
        await Expect(picker.Locator("[data-parameter=fallback]")).ToHaveValueAsync("null");

        await EditAsync(concat.Locator("[data-parameter=separator]"), "-");
        await EditAsync(concat.Locator("[data-parameter=repeat]"), "3");
        await concat.Locator("[data-parameter=casing]").SelectOptionAsync("upper");
        await picker.Locator("[data-parameter=caseSensitive]").UncheckAsync();
        await EditAsync(picker.Locator("[data-parameter=fallback]"), "{\"result\":17}");
        await EditAsync(concat.Locator("[data-parameter=repeat]"), "9");
        await Expect(concat.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Enter a value within the allowed range.");
        await EditAsync(picker.Locator("[data-parameter=fallback]"), "{bad");
        await Expect(picker.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Enter valid JSON.");

        await page.Locator("[data-language]").SelectOptionAsync("zh-Hans");
        await Expect(concatButton).ToHaveTextAsync("文本拼接");
        await Expect(concat.Locator("[data-port=left]")).ToContainTextAsync("左文本");
        await Expect(concat.GetByRole(AriaRole.Alert)).ToHaveTextAsync("请输入允许范围内的值。");
        await page.Locator("[data-language]").SelectOptionAsync("zh-Hant");
        await Expect(concatButton).ToHaveTextAsync("文字串接");
        await Expect(picker.Locator("[data-parameter=fallback]")).ToHaveAttributeAsync("aria-label", "備用 JSON");
        await page.ReloadAsync();
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("3");
        await Expect(concat.Locator("[data-parameter=separator]")).ToHaveValueAsync("-");
        await Expect(concat.Locator("[data-parameter=repeat]")).ToHaveValueAsync("3");
        await Expect(concat.Locator("[data-parameter=casing]")).ToHaveValueAsync("upper");
        await Expect(picker.Locator("[data-parameter=caseSensitive]")).Not.ToBeCheckedAsync();
        await Expect(picker.Locator("[data-parameter=fallback]")).ToHaveValueAsync("{\"result\":17}");

        if (WorkflowTests.EvidenceDirectory is { } output)
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "plugin-editor-zh-Hant.png") });
        await page.Locator("[data-language]").SelectOptionAsync("en");
        var download = await page.RunAndWaitForDownloadAsync(() =>
            page.GetByRole(AriaRole.Button, new() { Name = "Download JSON", Exact = true }).ClickAsync());
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.workflow.json");
        await download.SaveAsAsync(file);
        try
        {
            var json = await File.ReadAllTextAsync(file);
            var graph = WorkflowDocument.Read(json, NodeSdkTests.Samples().CreateCatalog());
            var node = graph.Nodes.Single(item => item.TypeId == "sample.concat");
            Assert.Equal(3, node.Config.Parameters!["repeat"].GetInt32());
            if (WorkflowTests.EvidenceDirectory is { } evidence)
                File.Copy(file, Path.Combine(evidence, "plugin-roundtrip.workflow.json"), overwrite: true);
            await page.Locator("input[type=file]").SetInputFilesAsync(file);
            await Expect(concat.Locator("[data-parameter=repeat]")).ToHaveValueAsync("3");
            node.Config.Parameters["repeat"] = JsonSerializer.SerializeToElement(9);
            await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
            {
                Name = "bad-plugin.workflow.json", MimeType = "application/json",
                Buffer = JsonSerializer.SerializeToUtf8Bytes(graph, WorkflowDocument.Json)
            });
            await Expect(page.Locator("[data-notice]")).ToHaveTextAsync("Enter a value within the allowed range.");
            await Expect(concat.Locator("[data-parameter=repeat]")).ToHaveValueAsync("3");
            await page.RouteAsync(Api, route => route.AbortAsync());
            await page.ReloadAsync();
            await Expect(concat.Locator("[data-parameter=repeat]")).ToHaveValueAsync("3");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task CatalogApi_ReturnsDllDefinitionsAndStableValidationIssues()
    {
        using var playwright = await Playwright.CreateAsync();
        var api = await playwright.APIRequest.NewContextAsync();
        try
        {
            var response = await api.GetAsync(Api);
            Assert.True(response.Ok, $"Catalog returned {response.Status}");
            var raw = await response.TextAsync();
            var definitions = JsonSerializer.Deserialize<NodeDefinition[]>(raw, WorkflowDocument.Json)!;
            Assert.Single(definitions, definition => definition.TypeId == "sample.concat");
            Assert.Single(definitions, definition => definition.TypeId == "sample.json-pick");
            var node = new WorkflowNode("test", "sample.concat", 1, new());
            var valid = await api.PostAsync(Api + "/validate", new()
            {
                Data = JsonSerializer.Serialize(node, WorkflowDocument.Json),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            });
            Assert.Equal(200, valid.Status);
            var result = JsonSerializer.Deserialize<ParameterValidation>(await valid.TextAsync(), WorkflowDocument.Json)!;
            Assert.Equal(1, result.Values["repeat"].GetInt32());
            var invalid = await api.PostAsync(Api + "/validate", new()
            {
                Data = JsonSerializer.Serialize(node with
                {
                    Config = new(Parameters: new() { ["repeat"] = JsonSerializer.SerializeToElement(9) })
                }, WorkflowDocument.Json),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
            });
            Assert.Equal(400, invalid.Status);
            var resultJson = await invalid.TextAsync();
            var issues = JsonSerializer.Deserialize<ParameterValidation>(resultJson, WorkflowDocument.Json)!.Issues;
            Assert.Equal(new NodeIssue("ParameterRange", "repeat"), Assert.Single(issues));
            await WorkflowTests.EvidenceAsync("validation-error.json", JsonSerializer.Deserialize<JsonElement>(resultJson));
        }
        finally { await api.DisposeAsync(); }
    }

    private static async Task EditAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.BlurAsync();
    }
}
