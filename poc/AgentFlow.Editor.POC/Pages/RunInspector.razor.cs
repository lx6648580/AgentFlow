using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AgentFlow.Editor.POC.Pages;

public partial class RunInspector
{
    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Parameter, EditorRequired] public HttpClient Api { get; set; } = default!;
    [Parameter] public string Culture { get; set; } = "en";
    [Parameter] public RunRecording? Live { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public bool Terminal { get; set; }
    [Parameter] public string ScopePath { get; set; } = "";
    [Parameter] public EventCallback<RunCanvasFrame?> CanvasChanged { get; set; }
    [Parameter] public EventCallback<bool> HistoryChanged { get; set; }
    [Parameter] public EventCallback<string> Locate { get; set; }

    private static readonly string[] QueueStates = ["Queued", "Running", "Paused", "Waiting", "Cancelling"];
    private static readonly string[] HistoryStates = ["Succeeded", "Failed", "Cancelled", "TimedOut"];
    private readonly Dictionary<Guid, string> artifacts = [];
    private string tab = "timeline", query = "", state = "", logQuery = "", kind = "";
    private bool busy;
    private bool wasVisible;
    private string? error;
    private RunSummary[] rows = [];
    private RunRecording? historical;
    private RunDefinition? definition;
    private string? graph;
    private string? selectedNode, iteration;
    private long? selectedSequence;
    private RunRecording? previous;
    private bool wasShowingHistory;

    private RunRecording? Recording => historical ?? Live;
    private IReadOnlyList<RunEventItem> Events => Recording?.Events ?? [];
    private string T(string key) => UiText.Keys.Contains(key) ? UiText.Get(key, Culture) : key;
    private string TabPressed(string key) => tab == key ? "true" : "false";

    protected override async Task OnParametersSetAsync()
    {
        var update = Visible && (!wasVisible || !ReferenceEquals(previous, Recording));
        wasVisible = Visible;
        if (update)
            await UpdateCanvasAsync();
    }

    private async Task UpdateCanvasAsync()
    {
        var showingHistory = historical is not null;
        if (Terminal && showingHistory != wasShowingHistory)
        {
            wasShowingHistory = showingHistory;
            await HistoryChanged.InvokeAsync(showingHistory);
        }
        previous = Recording;
        if (Recording is not { } recording)
        {
            await CanvasChanged.InvokeAsync(null);
            return;
        }
        if (!ReferenceEquals(definition, recording.Definition))
        {
            definition = recording.Definition;
            graph = RunCanvasFrame.GraphKey(definition.Workflow);
            ResetSelection();
        }
        await CanvasChanged.InvokeAsync(RunCanvasFrame.From(graph!, recording, selectedNode));
    }

    public async Task OpenTabAsync(string value)
    {
        if (busy)
            return;
        if (value != tab)
            state = "";
        tab = value;
        if (tab != "timeline")
            await LoadRunsAsync();
        else
            error = null;
    }

    public async Task InspectNodeAsync(string id)
    {
        if (Recording is not { } recording || !NodeEntries(recording.Definition.Workflow).Any(node => node.Path == id))
            return;
        if (!ReferenceEquals(previous, recording))
            await UpdateCanvasAsync();
        await OpenTabAsync("timeline");
        selectedNode = id;
        selectedSequence = null;
        iteration = null;
        await UpdateCanvasAsync();
        StateHasChanged();
    }

    private IEnumerable<(string Id, Guid? Artifact)> OutputReferences(RunRecording recording)
    {
        if (recording.Snapshot.Outputs is { } outputs)
            return outputs.Select(output => (output.NodeId, output.Value.ArtifactId));
        var ids = recording.Definition.Workflow.Nodes.Where(node => node.TypeId == NodeTypes.Output).Select(node => node.Id).ToHashSet();
        return Events.Where(item => item.Kind == "NodeCompleted" && item.NodeId is { } id && ids.Contains(id) && item.Value is not null)
            .DistinctBy(item => item.NodeId).Select(item => (item.NodeId!, item.Value!.ArtifactId));
    }

    private Task LoadRunsAsync() => RequestAsync(async token =>
    {
        var path = $"api/runs?queue={tab == "queue"}&q={Uri.EscapeDataString(query.Trim())}&state={Uri.EscapeDataString(state)}";
        rows = await Api.GetFromJsonAsync<RunSummary[]>(path, token) ?? [];
    });

    private Task OpenRecordingAsync(Guid id) => RequestAsync(async token =>
    {
        historical = await Api.GetFromJsonAsync<RunRecording>($"api/runs/{id}/recording", token)
            ?? throw new JsonException("Missing recording.");
        tab = "timeline";
        ResetSelection();
        await UpdateCanvasAsync();
    });

    private async Task FollowAsync()
    {
        if (historical is not null)
            ResetSelection();
        historical = null;
        error = null;
        await UpdateCanvasAsync();
    }

    private void ResetSelection()
    {
        selectedNode = null;
        selectedSequence = null;
        iteration = null;
        artifacts.Clear();
    }

    private async Task SelectEventAsync(RunEventItem item)
    {
        selectedSequence = item.Sequence;
        if (Terminal || item.NodeId is not null)
        {
            selectedNode = item.NodeId;
            iteration = item.IterationId;
        }
        await UpdateCanvasAsync();
        if (item.NodeId is { } id)
            await Locate.InvokeAsync(id);
    }

    private async Task SelectNodeAsync(ChangeEventArgs args)
    {
        selectedNode = args.Value?.ToString();
        selectedSequence = null;
        iteration = null;
        await UpdateCanvasAsync();
        if (!string.IsNullOrEmpty(selectedNode))
            await Locate.InvokeAsync(selectedNode);
    }

    private bool InScope(string? path) => ScopePath.Length == 0 || path?.StartsWith(ScopePath + "/", StringComparison.Ordinal) == true;

    private string ConsoleLine(RunRecording recording, RunEventItem item)
    {
        var ms = Math.Max(0, (item.At - recording.Snapshot.CreatedAt).TotalMilliseconds);
        var node = item.NodeId is { } id ? $"  {id}" : "";
        var duration = item.DurationMs is { } elapsed ? $"  ({elapsed:F0} ms)" : "";
        var batch = item.Batch is { } number ? $"  {T("BatchEvents")} {number + 1}" : "";
        var iteration = item.IterationId is { } value ? $"  {value}" : "";
        return FormattableString.Invariant($"+{ms,6:F0} ms  {T(item.Kind)}{node}{duration}{batch}{iteration}");
    }

    private IEnumerable<RunEventItem> FilteredEvents() => Events.Where(item =>
        InScope(item.NodeId) &&
        (kind switch
        {
            "nodes" => item.Kind.StartsWith("Node", StringComparison.Ordinal),
            "models" => item.Kind is "ModelRequest" or "ModelResult" or "RetryScheduled" or "ToolCall",
            "tools" => item.Kind is "ToolCall" or "ToolRequest" or "ToolResult" or "ToolRejected",
            "errors" => item.Kind is "NodeFailed" or "Failed" or "ArtifactUnavailable" or "TimedOut" or "ToolRejected",
            "batch" => item.Batch is not null || item.IterationId is not null,
            _ => true
        }) &&
        (string.IsNullOrWhiteSpace(logQuery) || (item.NodeId?.Contains(logQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
         item.Kind.Contains(logQuery, StringComparison.OrdinalIgnoreCase) || (item.Text?.Contains(logQuery, StringComparison.OrdinalIgnoreCase) ?? false)));

    private IEnumerable<RunEventItem> InspectedEvents()
    {
        var matches = Events.Where(item => item.NodeId == selectedNode &&
        (iteration is null || iteration == item.IterationId) && item.Kind is
            "NodeStarted" or "NodeCompleted" or "NodeBypassed" or "NodeCached" or "NodeFailed" or "NodeCancelled" or
            "NodeSkipped" or "HumanWaiting" or "HumanDecided" or "ModelRequest" or "ModelResult" or "ToolCall" or "RetryScheduled" or
            "ToolRequest" or "ToolResult" or "ToolRejected").ToArray();
        return matches.Where((item, index) => item.Sequence == selectedSequence || index >= matches.Length - 16);
    }

    private static IEnumerable<(string Path, WorkflowNode Node, WorkflowDocument Graph)> NodeEntries(
        WorkflowDocument graph, string prefix = "")
    {
        foreach (var node in graph.Nodes.Where(node => node.TypeId != NodeTypes.RunTerminal))
        {
            var path = prefix + node.Id;
            yield return (path, node, graph);
            if (node.TypeId == NodeTypes.Subworkflow &&
                graph.Subworkflows?.Find(child => SubworkflowEditing.References(node, child)) is { } subflow)
            {
                foreach (var entry in NodeEntries(subflow, path + "/"))
                    yield return entry;
            }
        }
    }

    private static bool StartsOpen(RunEventItem item) => item.Kind is
        "NodeStarted" or "NodeCompleted" or "NodeBypassed" or "NodeCached" or "NodeFailed" or "NodeCancelled";

    private string NodeLabel(WorkflowNode node)
    {
        try { return StandardNodes.Catalog.Get(node.TypeId, node.Version).Name?.Get(Culture, node.TypeId) ?? node.TypeId; }
        catch (GraphError) { return node.TypeId; }
    }

    private static ModelProfile? Profile(RunDefinition run, WorkflowDocument graph, WorkflowNode node)
    {
        var source = graph.Edges.FirstOrDefault(edge => edge.Target == node.Id && edge.Kind == "resource" && edge.TargetPort == "model")?.Source;
        var model = node.TypeId == NodeTypes.Model ? node : graph.Nodes.Find(item => item.Id == source);
        var id = model?.Config.Text?.Trim();
        if (id is null && model?.Config.Parameters?.TryGetValue("text", out var text) == true && text.ValueKind == JsonValueKind.String)
            id = text.GetString()?.Trim();
        return run.ModelProfiles?.FirstOrDefault(item => item.Id == id) ?? (run.ModelProfile?.Id == id ? run.ModelProfile : null);
    }

    private string FullValue(RunValue value) => value.ArtifactId is { } id && artifacts.TryGetValue(id, out var loaded)
        ? loaded : value.Preview;

    private Task LoadArtifactAsync(Guid artifact) => RequestAsync(async token =>
    {
        if (Recording is { } recording)
            artifacts[artifact] = await Api.GetStringAsync($"api/runs/{recording.Snapshot.RunId}/artifacts/{artifact}", token);
    });

    private Task ExportAsync() => RequestAsync(async token =>
    {
        if (Recording is not { } recording)
            return;
        var id = recording.Snapshot.RunId;
        var json = await Api.GetStringAsync($"api/runs/{id}/report", token);
        await Js.InvokeVoidAsync("agentFlow.download", token, $"run-{id:N}.json", json);
    });

    private async Task RequestAsync(Func<CancellationToken, Task> action)
    {
        if (busy)
            return;
        busy = true;
        error = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await action(timeout.Token); }
        catch (Exception issue) when (HostApi.IsNetworkError(issue) || issue is JSException) { error = "TraceUnavailable"; }
        finally { busy = false; }
    }
}
