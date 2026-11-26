using System.Text.Json;

namespace AgentFlow.Editor.POC;

public sealed record RunNodeFrame(string State, string? Error = null, RunOutput? Output = null, string? Preview = null);

public sealed record RunCanvasFrame(Guid RunId, string Graph, IReadOnlyDictionary<string, RunNodeFrame> Nodes, string? SelectedNode = null)
{
    public RunRecording? Recording { get; init; }

    public static string GraphKey(WorkflowDocument graph) => JsonSerializer.Serialize(new
    {
        graph.WorkflowId, graph.Revision, graph.Limits,
        Subworkflows = graph.Subworkflows?.OrderBy(child => child.WorkflowId, StringComparer.Ordinal).ThenBy(child => child.Revision).Select(GraphKey),
        Nodes = graph.Nodes.Where(node => node.TypeId != NodeTypes.RunTerminal).OrderBy(node => node.Id, StringComparer.Ordinal).Select(node => new
        {
            node.Id, node.TypeId, node.Version, node.Config.Text, node.Config.Ports,
            Parameters = node.Config.Parameters?.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            Mode = graph.Layout.GetValueOrDefault(node.Id)?.Mode
        }),
        Edges = graph.Edges.Select(edge => (edge.Source, edge.SourcePort, edge.Target, edge.TargetPort, edge.Kind))
            .OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.SourcePort, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal).ThenBy(edge => edge.TargetPort, StringComparer.Ordinal)
            .Select(edge => new { edge.Source, edge.SourcePort, edge.Target, edge.TargetPort, edge.Kind })
    });

    public static WorkflowDocument? ScopeWorkflow(WorkflowDocument graph, string scope)
    {
        foreach (var id in scope.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var call = graph.Nodes.Find(node => node.Id == id && node.TypeId == NodeTypes.Subworkflow);
            if (call is null || graph.Subworkflows?.Find(child => SubworkflowEditing.References(call, child)) is not { } subflow)
                return null;
            graph = subflow;
        }
        return graph;
    }

    public static RunCanvasFrame From(string graph, RunRecording recording, string? selected = null,
        WorkflowDocument? scope = null, string scopePath = "")
    {
        var definition = scope ?? recording.Definition.Workflow;
        var prefix = scopePath + "/";
        var outputs = definition.Nodes.Where(node => node.TypeId == NodeTypes.Output).Select(node => node.Id).ToHashSet();
        var metadata = (scope is null ? recording.Snapshot.Outputs ?? [] : []).ToDictionary(output => output.NodeId, StringComparer.Ordinal);
        var nodes = new Dictionary<string, RunNodeFrame>(StringComparer.Ordinal);
        foreach (var item in recording.Events)
        {
            if (item.Kind == "OutputDelta" && item.Text is { } delta)
            {
                var target = item.OutputNodeId ?? definition.Edges.FirstOrDefault(edge =>
                    edge.Source == item.NodeId && outputs.Contains(edge.Target))?.Target;
                if (target is not null)
                {
                    var current = nodes.GetValueOrDefault(target, new("Receiving"));
                    var preview = current.Preview ?? "";
                    if (current.Output is null && preview.Length < 1024)
                        nodes[target] = current with { Preview = preview + delta[..Math.Min(delta.Length, 1024 - preview.Length)] };
                }
            }
            if (item.NodeId is not { } id || scope is not null && !id.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (scope is not null)
                id = id[prefix.Length..];
            var state = item.Kind switch
            {
                "Paused" => "Paused",
                "HumanWaiting" => "Waiting",
                "NodeStarted" => "Running",
                "NodeCompleted" or "NodeBypassed" => "Completed",
                "NodeFailed" => "Failed",
                "NodeCancelled" => "Cancelled",
                "NodeSkipped" => "Skipped",
                "NodeCached" => "Cached",
                _ => null
            };
            if (state is null)
                continue;
            var previous = nodes.GetValueOrDefault(id, new("Idle"));
            var value = previous.Output;
            if (value is null && item.Kind == "NodeCompleted" && outputs.Contains(id) && item.Value is { } body)
                value = new(id, body, metadata.GetValueOrDefault(id)?.ContentType ?? "text/plain");
            nodes[id] = new(state, item.Kind == "NodeFailed" ? item.Text : null, value, value is null ? previous.Preview : null);
        }
        foreach (var output in metadata.Values)
        {
            var current = nodes.GetValueOrDefault(output.NodeId, new("Completed"));
            nodes[output.NodeId] = current with { Output = output, Preview = null };
        }
        var defaultState = recording.Snapshot.State switch
        {
            "Queued" => "Queued",
            "Succeeded" or "Failed" or "Cancelled" or "TimedOut" => "Skipped",
            _ => "Idle"
        };
        foreach (var node in definition.Nodes.Where(node => !NodeTypes.IsResource(node.TypeId) && node.TypeId != NodeTypes.RunTerminal))
        {
            if (!nodes.TryAdd(node.Id, new(defaultState)) && defaultState == "Skipped" && nodes[node.Id] is { Output: null } current)
                nodes[node.Id] = current with { State = current.State == "Receiving" ? "Skipped" : current.State, Preview = null };
        }
        var selection = scope is null ? selected : selected?.StartsWith(prefix, StringComparison.Ordinal) == true ? selected[prefix.Length..] : null;
        return new(recording.Snapshot.RunId, graph, nodes, selection) { Recording = recording };
    }
}
