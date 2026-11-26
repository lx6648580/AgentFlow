using System.Diagnostics;
using System.Text.Json;
using AgentFlow.Maf.POC;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

[Trait("Category", "Browser")]
public class EditorTests
{
    internal static string EditorUrl => Environment.GetEnvironmentVariable("AGENTFLOW_EDITOR_URL") ?? "http://127.0.0.1:5187/";
    internal static string? BrowserChannel => Environment.GetEnvironmentVariable("AGENTFLOW_BROWSER_CHANNEL");

    [Fact]
    public async Task BrowserDownload_RunsThroughMafCli()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = BrowserChannel });
        await using var context = await browser.NewContextAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        try
        {
            await OpenAsync(page);
            await page.Locator("input[type=file]").SetInputFilesAsync(WorkflowTests.SamplePath);
            await page.Locator("[data-node=input] textarea").FillAsync("  AgentFlow  ");
            var handle = page.Locator("[data-node=normalize] .node-handle strong");
            var before = await handle.BoundingBoxAsync();
            await DragAsync(page, handle, 22, 55);
            var after = await handle.BoundingBoxAsync();
            Assert.True(after!.Y - before!.Y >= 45, $"Node did not move: {before.Y} -> {after.Y}");

            var line = page.Locator("[data-link-id=e1] path").First;
            await line.WaitForAsync();
            var point = await line.EvaluateAsync<float[]>("""
                path => {
                    const p = path.getPointAtLength(path.getTotalLength() / 2).matrixTransform(path.getScreenCTM());
                    return [p.x, p.y];
                }
                """);
            await page.Mouse.ClickAsync(point[0], point[1]);
            await page.GetByRole(AriaRole.Button, new() { Name = "Delete selection" }).ClickAsync();
            await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("1");
            await ConnectAsync(page, "[data-node=input] [data-port=text]", "[data-node=normalize] [data-port=in]");
            await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("2");

            var exported = await DownloadAsync(page, "Download JSON");
            try
            {
                var result = await RunCliAsync(exported);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal("AgentFlow", JsonDocument.Parse(result.Output).RootElement.GetProperty("output").GetString());
                var events = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonSerializer.Deserialize<RunEvent>(line.TrimEnd(), WorkflowRunner.LogJson)!).ToArray();
                Assert.Equal(new[] { "input", "normalize", "output" }, events.Where(e => e.EventType == "ExecutorInvokedEvent").Select(e => e.NodeId));
                if (WorkflowTests.EvidenceDirectory is { } output)
                {
                    File.Copy(exported, Path.Combine(output, "browser-export.workflow.json"), overwrite: true);
                    await File.WriteAllTextAsync(Path.Combine(output, "browser-maf.ndjson"), result.Error);
                    await page.ScreenshotAsync(new() { Path = Path.Combine(output, "editor-en.png") });
                }
                await page.Locator("[data-language]").SelectOptionAsync("zh-Hant");
                await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", "zh-Hant");
                var translated = await DownloadAsync(page, "下載 JSON");
                try
                {
                    Assert.Equal(await File.ReadAllTextAsync(exported), await File.ReadAllTextAsync(translated));
                }
                finally { File.Delete(translated); }
            }
            finally { File.Delete(exported); }
            Assert.Empty(errors);
        }
        finally
        {
            await context.Tracing.StopAsync(new() { Path = WorkflowTests.EvidenceDirectory is { } output ? Path.Combine(output, "editor-e2e.zip") : null });
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP,zh-TW", "zh-Hant")]
    [InlineData("fr-FR", "en")]
    public async Task Startup_UsesBrowserLanguagePrecedence(string languages, string expected)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = languages.Split(',')[0] });
        await page.AddInitScriptAsync($"Object.defineProperty(navigator, 'languages', {{get: () => {JsonSerializer.Serialize(languages.Split(','))}}});");
        await OpenAsync(page);
        await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", expected);
        if (WorkflowTests.EvidenceDirectory is { } output && languages is "zh-CN" or "zh-HK")
        {
            await Expect(page.Locator("[data-kind='editor.run-terminal']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"editor-{expected}.png") });
        }
    }

    [Fact]
    public async Task LanguageChange_PreservesDraftAcrossReloadAndAllowsExportWhenStorageFails()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US" });
        await OpenAsync(page);
        await ImportMinimalAsync(page);
        await page.Locator("[data-node=input] textarea").FillAsync("草稿 Δ");
        await DragAsync(page, page.Locator("[data-node=normalize] .node-handle strong"), 0, 65);
        await page.Mouse.MoveAsync(600, 150);
        await page.Mouse.WheelAsync(0, -120);
        await page.WaitForFunctionAsync("async () => { const draft = JSON.parse(await agentFlow.readDraft()); return draft && draft.zoom !== 1; }");
        await page.Locator("[data-language]").SelectOptionAsync("zh-Hans");
        await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", "zh-Hans");
        var layer = page.Locator(".diagram-html-layer");
        var transform = await layer.EvaluateAsync<string>("element => element.style.transform");
        var savedDraft = await page.EvaluateAsync<string>("() => agentFlow.readDraft()");
        var before = await page.Locator("[data-node=normalize]").BoundingBoxAsync();
        await page.ReloadAsync();
        await Expect(page.Locator("[data-node=input] textarea")).ToHaveValueAsync("草稿 Δ");
        await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", "zh-Hans");
        var after = await page.Locator("[data-node=normalize]").BoundingBoxAsync();
        var restored = await layer.EvaluateAsync<string>("element => element.style.transform");
        Assert.True(Math.Abs(after!.Y - before!.Y) <= 2,
            $"Canvas y {before.Y} -> {after.Y}: {transform} -> {restored}\nDraft: {savedDraft}");
        await page.Locator("[data-language]").SelectOptionAsync("auto");
        await Expect(page.Locator(".studio")).ToHaveAttributeAsync("data-culture", "en");
        await page.EvaluateAsync("""
            () => {
                const transaction = IDBDatabase.prototype.transaction;
                IDBDatabase.prototype.transaction = function (stores, mode, options) {
                    if (mode === 'readwrite') throw new DOMException('storage probe', 'QuotaExceededError');
                    return transaction.call(this, stores, mode, options);
                };
            }
            """);
        await page.Locator("[data-node=input] textarea").FillAsync("still exportable");
        await Expect(page.Locator("[data-notice]")).ToContainTextAsync("Draft storage is unavailable");
        var path = await DownloadAsync(page, "Download JSON");
        try
        {
            Assert.Equal("still exportable", WorkflowDocument.Read(await File.ReadAllTextAsync(path)).Nodes[0].Config.Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Canvas_HandlesSearchSelectionFocusAndDynamicPorts()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        await OpenAsync(page);
        await ImportMinimalAsync(page);
        await page.Mouse.DblClickAsync(915, 490);
        var search = page.GetByRole(AriaRole.Textbox, new() { Name = "Search node types" });
        await Expect(search).ToBeFocusedAsync();
        await search.FillAsync("port probe");
        await search.PressAsync("Enter");
        var probe = page.Locator("[data-kind='poc.port-probe']");
        await Expect(probe).ToBeVisibleAsync();
        await probe.GetByRole(AriaRole.Button, new() { Name = "Add port" }).ClickAsync();
        await Expect(probe.Locator("[data-port=text-2] .flow-port")).ToBeVisibleAsync();

        await ConnectAsync(page, "[data-node=input] [data-port=text]", "[data-kind='poc.port-probe'] [data-port=number]");
        await Expect(page.Locator("[data-notice]")).ToHaveTextAsync("Connect ports of the same type.");
        await ConnectAsync(page, "[data-node=input] [data-port=text]", "[data-kind='poc.port-probe'] [data-port=text-2]");
        await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("3");
        await probe.GetByRole(AriaRole.Button, new() { Name = "Remove port" }).ClickAsync();
        await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync("2");

        var input = page.Locator("[data-node=input]");
        await input.Locator(".node-handle strong").ClickAsync();
        await page.Locator("[data-node=normalize] .node-handle strong").ClickAsync(new() { Modifiers = [KeyboardModifier.Control] });
        await Expect(page.Locator(".flow-node.selected")).ToHaveCountAsync(2);
        var text = input.Locator("textarea");
        var before = await input.BoundingBoxAsync();
        await text.ClickAsync();
        await text.PressAsync("Delete");
        await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("4");
        var after = await input.BoundingBoxAsync();
        Assert.Equal(before!.X, after!.X);

        await page.Keyboard.DownAsync("Shift");
        try
        {
            await page.Mouse.MoveAsync(235, 220);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(835, 440, new() { Steps = 15 });
            await page.Mouse.UpAsync();
        }
        finally { await page.Keyboard.UpAsync("Shift"); }
        await Expect(page.Locator(".flow-node.selected")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task Canvas_PanZoomAndResizeKeepPortAnchorsAligned()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = BrowserChannel });
        var page = await browser.NewPageAsync(new() { Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 } });
        await OpenAsync(page);
        await ImportMinimalAsync(page);
        var path = page.Locator("[data-link-id=e1] path").First;
        await path.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var input = page.Locator("[data-node=input]");
        var toggle = input.GetByRole(AriaRole.Button, new() { Name = "Expand or collapse" });
        var full = (await input.BoundingBoxAsync())!.Height;
        await toggle.ClickAsync();
        await Expect(input.Locator("textarea")).ToHaveCountAsync(0);
        Assert.True((await input.BoundingBoxAsync())!.Height < full);
        await AssertAnchorsAsync(page);
        await toggle.ClickAsync();
        await Expect(input.Locator("textarea")).ToBeVisibleAsync();

        var layer = page.Locator(".diagram-html-layer");
        var original = await layer.EvaluateAsync<string>("element => element.style.transform");
        await page.Mouse.MoveAsync(650, 520);
        await page.Mouse.WheelAsync(0, -120);
        await Expect(layer).Not.ToHaveAttributeAsync("style", $"transform: {original}; z-index: 0;");
        await AssertAnchorsAsync(page);

        await PanAsync(page, 1240, 600, 225);
        await Expect(input).ToHaveCountAsync(0);
        await PanAsync(page, 225, 600, 1240);
        await Expect(input).ToBeVisibleAsync();
        await AssertAnchorsAsync(page);
    }

    private static async Task PanAsync(IPage page, int startX, int y, int endX)
    {
        await page.Mouse.MoveAsync(startX, y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(endX, y, new() { Steps = 18 });
        await page.Mouse.UpAsync();
    }

    private static async Task AssertAnchorsAsync(IPage page)
    {
        await page.WaitForFunctionAsync("""
            () => {
                const path = document.querySelector('[data-link-id=e1] path');
                const from = document.querySelector('[data-node=input] .flow-port')?.getBoundingClientRect();
                const to = document.querySelector('[data-node=normalize] [data-port=in] .flow-port')?.getBoundingClientRect();
                if (!path || !from || !to) return false;
                const a = path.getPointAtLength(0).matrixTransform(path.getScreenCTM());
                const b = path.getPointAtLength(path.getTotalLength()).matrixTransform(path.getScreenCTM());
                const near = (p, box) => Math.hypot(p.x - box.x - box.width / 2, p.y - box.y - box.height / 2) < 16;
                return near(a, from) && near(b, to);
            }
            """, options: new() { Timeout = 5000 });
    }

    internal static async Task OpenAsync(IPage page)
    {
        var errors = new List<string>();
        void CaptureError(object? sender, IConsoleMessage message)
        {
            if (message.Type == "error")
                errors.Add(message.Text);
        }
        page.Console += CaptureError;
        await page.GotoAsync(EditorUrl);
        try
        {
            await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync("1", new() { Timeout = 20000 });
        }
        catch (PlaywrightException error) when (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors), error);
        }
        finally { page.Console -= CaptureError; }
        if (WorkflowTests.EvidenceDirectory is { } output)
            Directory.CreateDirectory(output);
    }

    internal static async Task ImportMinimalAsync(IPage page)
    {
        await page.Locator("input[type=file]").SetInputFilesAsync(WorkflowTests.SamplePath);
        await Expect(page.Locator("[data-node=normalize]")).ToBeVisibleAsync();
    }

    internal static async Task LoadExampleAsync(IPage page, string id)
    {
        await page.Locator("[data-open-examples]").ClickAsync();
        await page.Locator($"[data-example='{id}']").ClickAsync();
        await page.Locator("[data-load-example]").ClickAsync();
        await Expect(page.Locator("#workflow-examples")).ToBeHiddenAsync();
    }

    internal static async Task DragAsync(IPage page, ILocator locator, float x, float y)
    {
        var box = await locator.BoundingBoxAsync() ?? throw new InvalidOperationException("Drag source is not visible.");
        var startX = box.X + box.Width / 2;
        var startY = box.Y + box.Height / 2;
        await page.Mouse.MoveAsync(startX, startY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(startX + x, startY + y, new() { Steps = 14 });
        await page.Mouse.UpAsync();
    }

    private static async Task ConnectAsync(IPage page, string from, string to)
    {
        var source = await page.Locator(from + " .flow-port").BoundingBoxAsync();
        var target = await page.Locator(to + " .flow-port").BoundingBoxAsync();
        Assert.NotNull(source);
        Assert.NotNull(target);
        await page.Mouse.MoveAsync(source.X + source.Width / 2, source.Y + source.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(target.X + target.Width / 2, target.Y + target.Height / 2, new() { Steps = 18 });
        await page.Mouse.UpAsync();
    }

    private static async Task<string> DownloadAsync(IPage page, string label)
    {
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = label }).ClickAsync());
        var path = Path.Combine(Path.GetTempPath(), $"agentflow-{Guid.NewGuid():N}.json");
        await download.SaveAsAsync(path);
        return path;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(string file)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(WorkflowRunner).Assembly.Location);
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(file);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MAF POC.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
