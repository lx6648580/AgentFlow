using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;

namespace AgentFlow.Editor.POC.Pages;

public partial class RunPanel : IAsyncDisposable
{
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Parameter] public string Culture { get; set; } = "en";
    [Parameter] public bool Enabled { get; set; }
    [Parameter, EditorRequired] public Func<WorkflowDocument> Snapshot { get; set; } = default!;
    [Parameter] public string? WorkflowKey { get; set; }
    [Parameter] public EventCallback<RunCanvasFrame?> CanvasChanged { get; set; }
    [Parameter] public bool HasTerminal { get; set; }
    [Parameter] public string ScopePath { get; set; } = "";
    [Parameter] public EventCallback FocusTerminal { get; set; }
    [Parameter] public EventCallback<string> Locate { get; set; }

    private readonly CancellationTokenSource lifetime = new();
    private HttpClient? http;
    private HubConnection? hub;
    private Task? monitor;
    private Guid? currentId;
    private Guid? subscribedId;
    private RunSnapshot? current;
    private RunRequest? pending;
    private string? runError;
    private string? runErrorNode;
    private bool submitting;
    private bool cancelling;
    private bool refreshing;
    private bool controlling;
    private bool deciding;
    private string decisionActor = "", decisionValue = "";
    private bool inspectorOpen;
    private bool viewingHistory;
    public bool IsInspectorOpen => inspectorOpen;
    private RunInspector? inspector;
    private bool debugOptions;
    private bool pauseAtStart;
    private bool usePureCache;
    private string breakpointNode = "", stopAfterNode = "";
    private string? previousWorkflow;
    private RunDefinition? liveDefinition;
    private string? liveGraph;
    private RunRecording? LiveRecording { get; set; }
    private readonly List<RunEventItem> liveEvents = [];
    private long traceSequence;

    private string T(string key) => UiText.Get(key, Culture);
    private static string Flag(bool value) => value ? "true" : "false";
    private bool Active => currentId is not null && current?.State is not ("Succeeded" or "Failed" or "Cancelled" or "TimedOut");
    private bool PlayerReady => Enabled && !submitting && !viewingHistory && ScopePath.Length == 0;
    private bool InteractiveAvailable
    {
        get
        {
            var graph = Snapshot();
            return !graph.Nodes.Any(node => node.TypeId is (NodeTypes.Approval or NodeTypes.UserInput) &&
                graph.Layout.GetValueOrDefault(node.Id)?.Mode != "disabled");
        }
    }

    private void SetHistoryView(bool value) => viewingHistory = value;

    protected override void OnParametersSet()
    {
        if (previousWorkflow != WorkflowKey)
        {
            previousWorkflow = WorkflowKey;
            breakpointNode = stopAfterNode = "";
        }
        if (!HasTerminal)
            viewingHistory = false;
    }

    private IEnumerable<WorkflowNode> DebugNodes()
    {
        var graph = Snapshot();
        return graph.Nodes.Where(node => node.TypeId != NodeTypes.RunTerminal && !NodeTypes.IsResource(node.TypeId) &&
            graph.Layout.GetValueOrDefault(node.Id)?.Mode != "disabled");
    }

    protected override void OnInitialized()
    {
        if (HostApi.BaseAddress(Configuration["NodeCatalogUrl"]) is { } host)
            http = new HttpClient { BaseAddress = host, Timeout = Timeout.InfiniteTimeSpan };
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || http is null)
            return;
        try
        {
            if (Guid.TryParse(await Js.InvokeAsync<string?>("agentFlow.readRunId"), out var restored))
            {
                currentId = restored;
                StartMonitor();
                await Task.WhenAll(RefreshAsync(), ConnectAsync());
                StateHasChanged();
            }
        }
        catch (JSException error)
        {
            Console.Error.WriteLine(error.GetType().Name);
        }
    }

    private Task StartAsync() => SubmitAsync(step: false);

    private Task StepAsync() => current is { State: "Paused" } paused
        ? ControlAsync(paused.CanStepNode ? "stepNode" : "stepBatch")
        : SubmitAsync(step: true);

    private async Task SubmitAsync(bool step)
    {
        if (http is null)
        {
            runError = "RunHostUnavailable";
            return;
        }
        submitting = true;
        runError = runErrorNode = null;
        var debug = HasTerminal
            ? new RunDebugOptions { Interactive = InteractiveAvailable, StepAtStart = step }
            : new RunDebugOptions(pauseAtStart, breakpointNode.Length == 0 ? null : breakpointNode,
                stopAfterNode.Length == 0 ? null : stopAfterNode, usePureCache);
        var submission = pending ??= new(Snapshot(), Guid.NewGuid(), debug == new RunDebugOptions() ? null : debug);
        try
        {
            using var request = RequestTimeout();
            using var response = await http.PostAsJsonAsync("api/runs", submission, request.Token);
            if (!response.IsSuccessStatusCode)
            {
                (runError, runErrorNode) = await HostApi.ReadErrorAsync(response, request.Token, "RunRequestFailed", "RunNotFound");
                pending = null;
                return;
            }
            var started = await response.Content.ReadFromJsonAsync<RunStarted>(cancellationToken: request.Token);
            if (started?.RunId != submission.RequestId)
                throw new JsonException("Missing run acknowledgement.");
            currentId = started.RunId;
            current = null;
            viewingHistory = false;
            await ResetTraceAsync();
            pending = null;
            StartMonitor();
            try { await Js.InvokeVoidAsync("agentFlow.writeRunId", started.RunId.ToString()); }
            catch (JSException error) { Console.Error.WriteLine(error.GetType().Name); }
            await Task.WhenAll(RefreshAsync(), ConnectAsync());
        }
        catch (Exception error) when (HostApi.IsNetworkError(error))
        {
            runError = "RunHostUnavailable";
        }
        finally { submitting = false; }
    }

    private async Task ControlAsync(string command)
    {
        if (http is null || current is not { } run || (command == "pause" ? !run.CanPause : run.State != "Paused"))
            return;
        controlling = true;
        runError = runErrorNode = null;
        try
        {
            using var request = RequestTimeout();
            using var response = await http.PostAsJsonAsync($"api/runs/{run.RunId}/control",
                new RunControlRequest(command, run.Sequence), request.Token);
            if (response.IsSuccessStatusCode)
                ApplySnapshot(await response.Content.ReadFromJsonAsync<RunSnapshot>(cancellationToken: request.Token));
            else
                (runError, runErrorNode) = await HostApi.ReadErrorAsync(response, request.Token, "RunRequestFailed", "RunNotFound");
            await RefreshAsync();
        }
        catch (Exception error) when (HostApi.IsNetworkError(error)) { runError = "RunHostUnavailable"; }
        finally { controlling = false; }
    }

    private async Task DecideAsync(string action)
    {
        if (http is null || current is not { State: "Waiting", Waiting: { } wait } run)
            return;
        deciding = true;
        runError = runErrorNode = null;
        try
        {
            using var request = RequestTimeout();
            using var response = await http.PostAsJsonAsync($"api/runs/{run.RunId}/decision",
                new HumanDecision(wait.RequestId, decisionActor, action, action == "submit" ? decisionValue : null), request.Token);
            if (response.IsSuccessStatusCode)
                ApplySnapshot(await response.Content.ReadFromJsonAsync<RunSnapshot>(cancellationToken: request.Token));
            else
                (runError, runErrorNode) = await HostApi.ReadErrorAsync(response, request.Token, "RunRequestFailed", "RunNotFound");
            await RefreshAsync();
        }
        catch (Exception error) when (HostApi.IsNetworkError(error)) { runError = "RunHostUnavailable"; }
        finally { deciding = false; }
    }

    private async Task CancelAsync()
    {
        if (http is null || currentId is not { } id)
            return;
        cancelling = true;
        runError = runErrorNode = null;
        try
        {
            using var request = RequestTimeout();
            using var response = await http.PostAsync($"api/runs/{id}/cancel", null, request.Token);
            if (response.IsSuccessStatusCode)
                ApplySnapshot(await response.Content.ReadFromJsonAsync<RunSnapshot>(cancellationToken: request.Token));
            else
                (runError, runErrorNode) = await HostApi.ReadErrorAsync(response, request.Token, "RunRequestFailed", "RunNotFound");
        }
        catch (Exception error) when (HostApi.IsNetworkError(error)) { runError = "RunHostUnavailable"; }
        finally { cancelling = false; }
    }

    private void StartMonitor() => monitor ??= MonitorAsync();

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (!Active && (current is null || traceSequence >= current.Sequence))
                    continue;
                await InvokeAsync(async () =>
                {
                    await RefreshAsync();
                    await ConnectAsync();
                });
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ConnectAsync()
    {
        if (http is null || currentId is not { } id || lifetime.IsCancellationRequested)
            return;
        try
        {
            if (hub is null)
            {
                hub = new HubConnectionBuilder().WithUrl(new Uri(http.BaseAddress!, "hubs/runs"))
                    .WithAutomaticReconnect().Build();
                hub.On<Guid>("RunChanged", changed => changed == currentId ? InvokeAsync(RefreshAsync) : Task.CompletedTask);
                hub.Reconnected += _ => InvokeAsync(async () =>
                {
                    subscribedId = null;
                    await ConnectAsync();
                    await RefreshAsync();
                });
                hub.Closed += _ =>
                {
                    subscribedId = null;
                    return Task.CompletedTask;
                };
            }
            using var request = RequestTimeout();
            if (hub.State == HubConnectionState.Disconnected)
                await hub.StartAsync(request.Token);
            if (hub.State == HubConnectionState.Connected && subscribedId != id)
            {
                await hub.InvokeAsync("Subscribe", id, request.Token);
                subscribedId = id;
            }
        }
        catch (Exception error) when (HostApi.IsNetworkError(error) || error is InvalidOperationException or HubException)
        {
            // 定时快照读取同样能恢复连接间隙的输出。
            Console.Error.WriteLine($"Run hub: {error.GetType().Name}");
        }
    }

    private async Task RefreshAsync()
    {
        if (http is null || currentId is not { } id || refreshing || lifetime.IsCancellationRequested)
            return;
        refreshing = true;
        try
        {
            using var request = RequestTimeout();
            using var response = await http.GetAsync($"api/runs/{id}", request.Token);
            if (response.StatusCode == HttpStatusCode.NotFound && currentId == id)
            {
                currentId = null;
                current = null;
                await ResetTraceAsync();
                runError = "RunNotFound";
                StateHasChanged();
                return;
            }
            response.EnsureSuccessStatusCode();
            var snapshot = await response.Content.ReadFromJsonAsync<RunSnapshot>(cancellationToken: request.Token);
            ApplySnapshot(snapshot);
            if (snapshot is not null && currentId == id && (liveDefinition is null || traceSequence < snapshot.Sequence))
                await RefreshTraceAsync(id, request.Token);
        }
        catch (Exception error) when (HostApi.IsNetworkError(error))
        {
            if (currentId == id && runError != "RunDisconnected" && !lifetime.IsCancellationRequested)
            {
                runError = "RunDisconnected";
                StateHasChanged();
            }
        }
        finally { refreshing = false; }
    }

    private async Task RefreshTraceAsync(Guid id, CancellationToken token)
    {
        var definition = liveDefinition ?? await http!.GetFromJsonAsync<RunDefinition>($"api/runs/{id}/definition", token);
        var updates = await http!.GetFromJsonAsync<RunEventItem[]>($"api/runs/{id}/events?after={traceSequence}", token);
        if (currentId != id || definition is null || updates is null || current is null)
            return;
        if (liveDefinition is null)
        {
            liveDefinition = definition;
            liveGraph = RunCanvasFrame.GraphKey(definition.Workflow);
        }
        liveEvents.AddRange(updates.Where(item => item.Sequence > traceSequence));
        if (updates.Length > 0)
            traceSequence = updates[^1].Sequence;
        if (liveEvents.Count > 4096)
            liveEvents.RemoveRange(0, liveEvents.Count - 4096);
        LiveRecording = new(current, definition, liveEvents.ToArray());
        if (!inspectorOpen && !HasTerminal)
            await CanvasChanged.InvokeAsync(RunCanvasFrame.From(liveGraph!, LiveRecording));
        StateHasChanged();
    }

    private async Task ResetTraceAsync()
    {
        liveDefinition = null;
        liveGraph = null;
        traceSequence = 0;
        liveEvents.Clear();
        LiveRecording = null;
        await CanvasChanged.InvokeAsync(null);
    }

    private async Task ToggleInspectorAsync()
    {
        inspectorOpen = !inspectorOpen;
        if (HasTerminal)
        {
            if (inspectorOpen && inspector is not null)
                await inspector.OpenTabAsync("history");
            return;
        }
        if (!inspectorOpen && liveGraph is not null && LiveRecording is not null)
            await CanvasChanged.InvokeAsync(RunCanvasFrame.From(liveGraph, LiveRecording));
        else if (inspectorOpen && inspector is not null)
            await inspector.OpenTabAsync("timeline");
    }

    private async Task OpenQueueAsync()
    {
        inspectorOpen = true;
        if (inspector is not null)
            await inspector.OpenTabAsync("queue");
    }

    public async Task InspectNodeAsync(string nodeId)
    {
        inspectorOpen = true;
        if (inspector is not null)
            await inspector.InspectNodeAsync(nodeId);
        StateHasChanged();
    }

    private void ApplySnapshot(RunSnapshot? result)
    {
        if (result is null || result.RunId != currentId || current?.Sequence > result.Sequence)
            return;
        var changed = current?.Sequence != result.Sequence;
        if (current?.Waiting?.RequestId != result.Waiting?.RequestId)
            decisionActor = decisionValue = "";
        current = result;
        if (runError == "RunDisconnected")
        {
            runError = null;
            changed = true;
        }
        if (changed && !lifetime.IsCancellationRequested)
            StateHasChanged();
    }

    private CancellationTokenSource RequestTimeout()
    {
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(4));
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (monitor is not null)
            await monitor;
        if (hub is not null)
            await hub.DisposeAsync();
        http?.Dispose();
        lifetime.Dispose();
    }
}
