using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AgentFlow.Phase00.Tests;

[Trait("Category", "Benchmark")]
public class BenchmarkTests
{
    [Fact]
    public async Task Editor_RecordsRepeatableBrowserFrameSamples()
    {
        var output = WorkflowTests.EvidenceDirectory ?? throw new InvalidOperationException("Set AGENTFLOW_EVIDENCE.");
        Directory.CreateDirectory(output);
        var filters = (Environment.GetEnvironmentVariable("AGENTFLOW_BENCH_FILTER") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string key) => filters.All(filter => key.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var windowsDesktop = Environment.GetEnvironmentVariable("AGENTFLOW_BENCH_DESKTOP") == "1";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = EditorTests.BrowserChannel, Headless = !windowsDesktop,
            Args = windowsDesktop ? ["--window-position=20,20", "--window-size=1500,985"] : []
        });
        await using var context = await browser.NewContextAsync(new()
        {
            Locale = "en-US", ViewportSize = new() { Width = 1450, Height = 900 }
        });
        var page = await context.NewPageAsync();
        var startup = Stopwatch.StartNew();
        await EditorTests.OpenAsync(page);
        startup.Stop();
        var cdp = await context.NewCDPSessionAsync(page);
        var idleFrameMs = await page.EvaluateAsync<double>("""
            () => new Promise(resolve => {
                const samples = [];
                let previous;
                function next(now) {
                    if (previous !== undefined) samples.push(now - previous);
                    previous = now;
                    if (samples.length < 90) requestAnimationFrame(next);
                    else resolve(samples.sort((a, b) => a - b)[45]);
                }
                requestAnimationFrame(next);
            })
            """);

        var environment = await page.EvaluateAsync<JsonElement>("""
            () => {
                const screenInfo = { width: screen.width, height: screen.height, dpr: devicePixelRatio };
                const gl = document.createElement('canvas').getContext('webgl');
                const ext = gl?.getExtension('WEBGL_debug_renderer_info');
                return { screen: screenInfo, cpuThreads: navigator.hardwareConcurrency,
                         memoryGb: navigator.deviceMemory, gpu: ext && gl.getParameter(ext.UNMASKED_RENDERER_WEBGL),
                         userAgent: navigator.userAgent, navigation: performance.getEntriesByType('navigation')[0]?.toJSON() };
            }
            """);
        await WorkflowTests.EvidenceAsync("benchmark-environment.json", new
        {
            browser = browser.Version, headless = !windowsDesktop, initialGraphMs = startup.Elapsed.TotalMilliseconds,
            os = Environment.OSVersion.VersionString, cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            idleFrameMs, environment
        });

        var results = new List<object>();
        foreach (var count in new[] { 50, 100 })
        foreach (var virtualization in new[] { true, false })
        foreach (var view in new[] { "fit", "partial" })
        foreach (var action in new[] { "drag", "pan", "zoom", "status" })
        {
            var name = $"{count}-{view}-{(virtualization ? "virtual" : "all")}-{action}";
            if (!Matches(name))
                continue;
            var samples = new List<Sample>();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await page.Locator(".virtual input").SetCheckedAsync(virtualization);
                if (await page.Locator(".sidebar details").GetAttributeAsync("open") is null)
                    await page.Locator(".sidebar summary").ClickAsync();
                await page.Locator($"[data-bench='{count}']").ClickAsync();
                await Expect(page.Locator("[data-node-count]")).ToHaveTextAsync(count.ToString());
                await Expect(page.Locator("[data-link-count]")).ToHaveTextAsync((count * 8 / 5).ToString());
                if (view == "fit")
                    await FitAllNodesAsync(page, count);
                await page.Locator("[data-node=bench-0]").WaitForAsync();
                var bounds = await page.Locator(".canvas-host").BoundingBoxAsync() ?? throw new InvalidOperationException("Canvas is not visible.");
                var handle = await page.Locator("[data-node=bench-0] .node-handle strong").BoundingBoxAsync()
                    ?? throw new InvalidOperationException("Benchmark drag source is not visible.");
                var x = action is "drag" or "status" ? handle.X + handle.Width / 2 : bounds.X + 12;
                var y = action is "drag" or "status" ? handle.Y + handle.Height / 2 : bounds.Y + 12;
                var visible = await page.EvaluateAsync<int>(CountVisibleNodes);
                Assert.InRange(visible, view == "fit" ? count : 1, view == "fit" ? count : count - 1);
                var duration = attempt == 0 ? 1_000 : 10_000;
                if (attempt == 1)
                    await cdp.SendAsync("Tracing.start", new Dictionary<string, object>
                    {
                        ["categories"] = "devtools.timeline,blink.user_timing,disabled-by-default-devtools.timeline.frame",
                        ["transferMode"] = "ReturnAsStream"
                    });
                await StartSamplingAsync(page, action);
                var updates = await RepeatGestureAsync(page, action, x, y, duration);
                var browserSample = await page.EvaluateAsync<JsonElement>("""
                    () => window.agentFlowSampling.finish()
                    """);
                var measurement = browserSample.Deserialize<BrowserSample>(WorkflowDocument.Json)
                    ?? throw new InvalidOperationException("Browser sample was missing.");
                if (attempt == 1)
                    await SaveTraceAsync(cdp, Path.Combine(output, $"trace-{name}.json.gz"));
                if (attempt == 0)
                    continue;
                var sorted = measurement.Frames.Order().ToArray();
                Assert.NotEmpty(sorted);
                var sample = new Sample(attempt, visible, updates, sorted.Length * 1000 / measurement.Elapsed,
                    sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
                    measurement);
                samples.Add(sample);
                await WorkflowTests.EvidenceAsync($"frames-{name}-{attempt}.json", sample);
            }
            var fpsTarget = count == 50 ? 55 : 45;
            var p95Target = count == 50 ? 25.0 : 33.3;
            var meetsThreshold = samples.All(s => s.Fps >= fpsTarget) && samples.Select(s => s.P95Ms).Order().ElementAt(1) <= p95Target;
            results.Add(new
            {
                name, virtualization, view, action, count, meetsThreshold,
                acceptance = windowsDesktop && idleFrameMs is >= 15 and <= 18 && virtualization && action is ("drag" or "pan"),
                runs = samples.Select(s => new { s.Attempt, s.Visible, s.StatusBatches, s.Fps, s.P95Ms })
            });
            await WorkflowTests.EvidenceAsync("benchmark-summary.json", results);
        }
        Assert.NotEmpty(results);
    }

    private const string CountVisibleNodes = """
        () => {
            const canvas = document.querySelector('.canvas-host').getBoundingClientRect();
            return [...document.querySelectorAll('.flow-node')].filter(node => {
                const r = node.getBoundingClientRect();
                return r.right > canvas.left && r.left < canvas.right && r.bottom > canvas.top && r.top < canvas.bottom;
            }).length;
        }
        """;

    private static async Task FitAllNodesAsync(IPage page, int count)
    {
        var fit = page.GetByRole(AriaRole.Button, new() { Name = "Fit graph" });
        // 新图的端口和节点尺寸由浏览器异步测量。进入计时前确认视口确已覆盖全图。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await fit.ClickAsync();
            try
            {
                await page.WaitForFunctionAsync($"count => ({CountVisibleNodes})() === count", count, new() { Timeout = 2000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                // 首次布局可能仍在更新，等待后用已测量的节点重新适配。
            }
        }
    }

    private static async Task StartSamplingAsync(IPage page, string action)
    {
        await page.EvaluateAsync("""
            action => {
                const container = document.querySelector('.canvas-host');
                const observed = action === 'drag' || action === 'status'
                    ? document.querySelector('[data-node=bench-0]').closest('.diagram-node')
                    : container.querySelector('.diagram-node');
                const frames = [], inputLatency = [];
                let lastInput, lastFrame, id;
                const started = performance.now();
                const input = () => lastInput = performance.now();
                const event = action === 'zoom' ? 'wheel' : 'pointermove';
                container.addEventListener(event, input, { capture: true, passive: true });
                const observer = new MutationObserver(() => {
                    if (lastInput !== undefined) {
                        inputLatency.push(performance.now() - lastInput);
                        lastInput = undefined;
                    }
                });
                if (observed) {
                    observer.observe(observed, { attributes: true, attributeFilter: ['style'] });
                    if (observed.parentElement)
                        observer.observe(observed.parentElement, { attributes: true, attributeFilter: ['style'] });
                }
                function frame(now) {
                    if (lastFrame !== undefined) frames.push(now - lastFrame);
                    lastFrame = now;
                    id = requestAnimationFrame(frame);
                }
                id = requestAnimationFrame(frame);
                window.agentFlowSampling = { finish() {
                    cancelAnimationFrame(id);
                    observer.disconnect();
                    container.removeEventListener(event, input, true);
                    return { frames, inputLatency, elapsed: performance.now() - started,
                             heapBytes: performance.memory?.usedJSHeapSize ?? null };
                } };
            }
            """, action);
    }

    private static async Task<int> RepeatGestureAsync(IPage page, string action, float x, float y, int durationMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextBatch = 0d;
        var batches = 0;
        var wheelDirection = -1;
        await page.Mouse.MoveAsync(x, y);
        if (action != "zoom")
            await page.Mouse.DownAsync();
        try
        {
            while (stopwatch.ElapsedMilliseconds < durationMs)
            {
                var elapsed = stopwatch.Elapsed.TotalMilliseconds;
                if (action == "status" && elapsed >= nextBatch)
                {
                    await page.EvaluateAsync("() => agentFlow.benchmarkUpdate()");
                    nextBatch += 100;
                    batches++;
                }
                if (action == "zoom")
                {
                    await page.Mouse.WheelAsync(0, wheelDirection * 35);
                    wheelDirection *= -1;
                }
                else
                    await page.Mouse.MoveAsync(x + 24 + 22 * (float)Math.Sin(elapsed / 370),
                        y + 15 + 12 * (float)Math.Sin(elapsed / 240));
                await Task.Delay(18);
            }
        }
        finally
        {
            if (action != "zoom")
                await page.Mouse.UpAsync();
        }
        return batches;
    }

    private static async Task SaveTraceAsync(ICDPSession cdp, string path)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        cdp.Event("Tracing.tracingComplete").OnEvent += (_, value) =>
            completion.TrySetResult(value!.Value.GetProperty("stream").GetString()!);
        await cdp.SendAsync("Tracing.end");
        var handle = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using var file = File.Create(path);
        await using var compressed = new GZipStream(file, CompressionLevel.Fastest);
        try
        {
            while (true)
            {
                var result = (await cdp.SendAsync("IO.read", new Dictionary<string, object> { ["handle"] = handle }))!.Value;
                var data = result.GetProperty("data").GetString()!;
                var bytes = result.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean()
                    ? Convert.FromBase64String(data) : System.Text.Encoding.UTF8.GetBytes(data);
                await compressed.WriteAsync(bytes);
                if (result.GetProperty("eof").GetBoolean())
                    break;
            }
        }
        finally { await cdp.SendAsync("IO.close", new Dictionary<string, object> { ["handle"] = handle }); }
    }

    public sealed record BrowserSample(double[] Frames, double[] InputLatency, double Elapsed, long? HeapBytes);
    public sealed record Sample(int Attempt, int Visible, int StatusBatches, double Fps, double P95Ms, BrowserSample Raw);
}
