using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

[Trait("Category", "Browser")]
public class EditorV01Tests
{
    [Fact]
    public async Task Editor_BuildsTwentyNodesAndRoundTripsGroupsLinksAndParameters()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        await EditorTests.OpenAsync(page);
        await Button(page, "New workflow").ClickAsync();
        var count = page.Locator("[data-node-count]");
        await Expect(count).ToHaveTextAsync("1");

        await page.Mouse.ClickAsync(265, 240, new() { Button = MouseButton.Right });
        await Expect(page.GetByRole(AriaRole.Dialog, new() { Name = "Canvas actions" })).ToBeVisibleAsync();
        await page.Locator("[data-search]").GetByRole(AriaRole.Button, new() { Name = "Add node" }).ClickAsync();
        var search = page.GetByRole(AriaRole.Textbox, new() { Name = "Search node types" });
        await search.FillAsync("text input");
        await search.PressAsync("Enter");
        await Library(page, "ai.prompt").DragToAsync(page.Locator(".canvas-host"),
            new() { TargetPosition = new() { X = 420, Y = 140 } });
        await Expect(count).ToHaveTextAsync("3");

        var text = page.Locator("[data-kind='core.text-input']");
        var prompt = page.Locator("[data-kind='ai.prompt']");
        await text.Locator("textarea").FillAsync("phase01 review");
        await LinkAsync(page, text.Locator(".port-row.out .flow-port"), prompt.Locator(".port-row.in .flow-port"));
        await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("1");
        await LinkAsync(page, prompt.Locator(".port-row.out .flow-port"), null);
        await Expect(search).ToBeFocusedAsync();
        await Expect(page.Locator("[data-search] [data-type='ai.chat-model']")).ToHaveCountAsync(0);
        await search.FillAsync("debug");
        await search.PressAsync("Enter");
        await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("2");

        await text.Locator(".node-handle strong").ClickAsync();
        await prompt.Locator(".node-handle strong").ClickAsync(new() { Modifiers = [KeyboardModifier.Control] });
        await page.Keyboard.PressAsync("Control+g");
        await Expect(page.Locator(".diagram-group")).ToHaveCountAsync(1);
        var start = (await text.BoundingBoxAsync())!;
        var group = (await page.Locator(".diagram-group").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(group.X + 7, group.Y + 7);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(group.X + 31, group.Y + 34, new() { Steps = 14 });
        await page.Mouse.UpAsync();
        var moved = (await text.BoundingBoxAsync())!;
        Assert.True(moved.X - start.X >= 20);
        await page.Keyboard.PressAsync("Control+c");
        await page.Keyboard.PressAsync("Control+v");
        await Expect(count).ToHaveTextAsync("6");
        await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("3");
        await Button(page, "Undo").ClickAsync();
        await Expect(count).ToHaveTextAsync("4");
        await Button(page, "Redo").ClickAsync();
        await Expect(count).ToHaveTextAsync("6");
        await Button(page, "Undo").ClickAsync();
        await Expect(count).ToHaveTextAsync("4");

        string[] types = ["ai.chat-model", "ai.agent", "flow.condition", "flow.parallel", "flow.merge", "core.output", "core.debug"];
        for (var index = 3; index < 20; index++)
        {
            await Library(page, types[index % types.Length]).ClickAsync();
            await Expect(count).ToHaveTextAsync((index + 2).ToString());
        }
        await Button(page, "Fit graph").ClickAsync();
        await Button(page, "Fit graph").ClickAsync();
        var working = await DownloadAsync(page, "Download JSON");
        page.Dialog += (_, dialog) => dialog.AcceptAsync("review-twenty");
        var saved = await DownloadAsync(page, "Save as");
        try
        {
            var original = WorkflowDocument.Read(await File.ReadAllTextAsync(working));
            var expected = WorkflowDocument.Read(await File.ReadAllTextAsync(saved));
            Assert.Equal(20, expected.Nodes.Count(node => node.TypeId != NodeTypes.RunTerminal));
            Assert.Equal(2, expected.Edges.Count);
            Assert.Single(expected.Groups!);
            Assert.Equal("phase01 review", expected.Nodes.Single(node => node.TypeId == NodeTypes.Input).Config.Text);
            Assert.Equal(original.Layout, expected.Layout);
            var tab = page.Locator(".workflow-tab.current");
            await Expect(tab).ToContainTextAsync("review-twenty");
            await Expect(tab.Locator(".tab-dirty")).ToHaveCountAsync(0);
            if (WorkflowTests.EvidenceDirectory is { } output)
            {
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, "editor-v01.png") });
                File.Copy(saved, Path.Combine(output, "twenty.workflow.json"), overwrite: true);
            }
            await tab.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
            await Expect(count).ToHaveTextAsync("1");
            await page.Locator("input[type=file]").SetInputFilesAsync(saved);
            await Expect(count).ToHaveTextAsync("21");
            var reopened = await DownloadAsync(page, "Download JSON");
            try { Assert.Equal(await File.ReadAllTextAsync(saved), await File.ReadAllTextAsync(reopened)); }
            finally { File.Delete(reopened); }
        }
        finally
        {
            File.Delete(working);
            File.Delete(saved);
        }
    }

    [Fact]
    public async Task Workspace_ReloadsTabsAndHistoryAndProtectsUnsavedEdits()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US" });
        await EditorTests.OpenAsync(page);
        await Library(page, "core.text-input").ClickAsync();
        var sample = page.Locator("[data-kind='core.text-input'] textarea");
        await sample.FillAsync("first tab");
        await Button(page, "New workflow").ClickAsync();
        await Library(page, "core.text-input").ClickAsync();
        await Library(page, "core.output").ClickAsync();
        await Button(page, "Undo").ClickAsync();
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("2");
        await page.Locator("[data-language]").SelectOptionAsync("zh-Hant");
        await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", "zh-Hant");
        await page.ReloadAsync();
        await Expect(page.Locator(".workflow-tab")).ToHaveCountAsync(2);
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("2");
        await Button(page, "取消復原").ClickAsync();
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("3");
        await page.Locator(".workflow-tab.current").GetByRole(AriaRole.Button, new() { Name = "關閉" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Alertdialog);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "取消" }).ClickAsync();
        await Expect(page.Locator(".workflow-tab")).ToHaveCountAsync(2);
        await page.Locator(".workflow-tab.current").GetByRole(AriaRole.Button, new() { Name = "關閉" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "捨棄變更" }).ClickAsync();
        await Expect(sample).ToHaveValueAsync("first tab");
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "bad.workflow.json", MimeType = "application/json", Buffer = System.Text.Encoding.UTF8.GetBytes("{")
        });
        await Expect(page.Locator("[data-notice]")).ToContainTextAsync("工作流程檔案無效");
        await Expect(sample).ToHaveValueAsync("first tab");
    }

    [Fact]
    public async Task Canvas_LockAndCollapseSurviveDownload()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US" });
        await EditorTests.OpenAsync(page);
        await EditorTests.ImportMinimalAsync(page);
        var node = page.Locator("[data-node=input]");
        var header = node.Locator(".node-handle strong");
        await header.ClickAsync(new() { Button = MouseButton.Right });
        await page.Locator("[data-search]").GetByRole(AriaRole.Button, new() { Name = "Lock", Exact = true }).ClickAsync();
        await Expect(node).ToHaveAttributeAsync("data-locked", "true");
        await page.Locator("[data-search]").GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        var before = await header.BoundingBoxAsync();
        await EditorTests.DragAsync(page, header, 45, 30);
        var after = await header.BoundingBoxAsync();
        Assert.Equal(before!.X, after!.X);
        Assert.Equal(before.Y, after.Y);
        await node.GetByRole(AriaRole.Button, new() { Name = "Expand or collapse" }).ClickAsync();
        await Expect(node.Locator("textarea")).ToHaveCountAsync(0);
        await header.ClickAsync();
        await page.Keyboard.PressAsync("Control+b");
        await Expect(node).ToHaveAttributeAsync("data-mode", "bypass");
        var file = await DownloadAsync(page, "Download JSON");
        try
        {
            var exported = WorkflowDocument.Read(await File.ReadAllTextAsync(file));
            Assert.True(exported.Layout["input"].Locked);
            Assert.True(exported.Layout["input"].Collapsed);
            Assert.Equal("bypass", exported.Layout["input"].Mode);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Canvas_ReconnectsSelectedLinkTarget()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = EditorTests.BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        await EditorTests.OpenAsync(page);
        await EditorTests.ImportMinimalAsync(page);
        await Library(page, "core.debug").DragToAsync(page.Locator(".canvas-host"),
            new() { TargetPosition = new() { X = 845, Y = 465 } });
        var path = page.Locator("[data-link-id=e1] path").First;
        await path.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var point = await path.EvaluateAsync<float[]>("""
            element => {
                const mid = element.getPointAtLength(element.getTotalLength() / 2).matrixTransform(element.getScreenCTM());
                return [mid.x, mid.y];
            }
            """);
        await page.Mouse.ClickAsync(point[0], point[1]);
        var head = page.Locator(".diagram-control").First;
        await head.WaitForAsync(new() { Timeout = 5000 });
        await LinkAsync(page, head, null);
        await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("2");
        if (!await head.IsVisibleAsync())
        {
            var restoredPoint = await path.EvaluateAsync<float[]>("""
                element => {
                    const mid = element.getPointAtLength(element.getTotalLength() / 2).matrixTransform(element.getScreenCTM());
                    return [mid.x, mid.y];
                }
                """);
            await page.Mouse.ClickAsync(restoredPoint[0], restoredPoint[1]);
        }
        await head.WaitForAsync(new() { Timeout = 5000 });
        await LinkAsync(page, head, page.Locator("[data-kind='core.debug'] [data-port=in] .flow-port"));
        var raw = await DownloadAsync(page, "Download JSON");
        try
        {
            var graph = WorkflowDocument.Read(await File.ReadAllTextAsync(raw));
            Assert.Equal(2, graph.Edges.Count);
            Assert.Equal(graph.Nodes.Single(node => node.TypeId == NodeTypes.Debug).Id,
                graph.Edges.Single(edge => edge.Id == "e1").Target);
            var transform = await page.Locator(".diagram-html-layer").EvaluateAsync<string>("element => element.style.transform");
            await page.Locator("[data-minimap]").ClickAsync(new() { Position = new() { X = 130, Y = 65 } });
            await page.WaitForFunctionAsync("old => document.querySelector('.diagram-html-layer').style.transform !== old", transform,
                new() { Timeout = 5000 });
        }
        finally { File.Delete(raw); }
    }

    private static ILocator Library(IPage page, string type) => page.Locator($"[data-library='{type}']");

    private static ILocator Button(IPage page, string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static async Task<string> DownloadAsync(IPage page, string label)
    {
        var download = await page.RunAndWaitForDownloadAsync(() => Button(page, label).ClickAsync());
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.workflow.json");
        await download.SaveAsAsync(path);
        return path;
    }

    private static async Task LinkAsync(IPage page, ILocator source, ILocator? target)
    {
        var from = await source.BoundingBoxAsync();
        var to = target is null ? null : await target.BoundingBoxAsync();
        Assert.NotNull(from);
        await page.Mouse.MoveAsync(from.X + from.Width / 2, from.Y + from.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(to is null ? 950 : to.X + to.Width / 2,
            to is null ? 420 : to.Y + to.Height / 2, new() { Steps = 20 });
        await page.Mouse.UpAsync();
    }
}
