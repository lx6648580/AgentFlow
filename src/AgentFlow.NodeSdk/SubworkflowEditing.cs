using System.Text.Json;

namespace AgentFlow;

public static class SubworkflowEditing
{
    public static WorkflowDocument Encapsulate(WorkflowDocument graph, IReadOnlySet<string> selection, string id)
    {
        selection = graph.Nodes.Where(node => node.TypeId != NodeTypes.RunTerminal && selection.Contains(node.Id))
            .Select(node => node.Id).ToHashSet();
        var nodes = graph.Nodes.Where(node => selection.Contains(node.Id)).ToList();
        if (nodes.Count == 0 || nodes.Any(node => node.TypeId is NodeTypes.Input or NodeTypes.Output or NodeTypes.Approval or NodeTypes.UserInput or NodeTypes.SimulatedWrite))
            throw new GraphError("InvalidSubworkflowSelection");
        var incoming = graph.Edges.Where(edge => !selection.Contains(edge.Source) && selection.Contains(edge.Target)).ToArray();
        var outgoing = graph.Edges.Where(edge => selection.Contains(edge.Source) && !selection.Contains(edge.Target)).ToArray();
        if (incoming is not [{ Kind: "message" } entry] || outgoing is not [{ Kind: "message" } exit])
            throw new GraphError("InvalidSubworkflowBoundary");

        var start = id + "-in";
        var end = id + "-out";
        var layout = graph.Layout.Where(pair => selection.Contains(pair.Key)).ToDictionary();
        var x = layout.Values.Select(point => point.X).DefaultIfEmpty(250).Min();
        var y = layout.Values.Select(point => point.Y).DefaultIfEmpty(200).Min();
        layout.Add(start, new(x - 280, y));
        layout.Add(end, new(layout.Values.Max(point => point.X) + 280, y));
        nodes.Insert(0, new(start, NodeTypes.Input, 1, new("Subworkflow input")));
        nodes.Add(new(end, NodeTypes.Output, 1, new()));
        var childEdges = graph.Edges.Where(edge => selection.Contains(edge.Source) && selection.Contains(edge.Target)).ToList();
        childEdges.Add(entry with { Source = start, SourcePort = "text" });
        childEdges.Add(exit with { Target = end, TargetPort = "value" });
        var children = graph.Subworkflows?.Where(child => nodes.Any(node => References(node, child))).ToList();
        var child = new WorkflowDocument("0.1", id, 1, nodes, childEdges, layout, Subworkflows: children is { Count: > 0 } ? children : null);

        var remaining = graph.Nodes.Where(node => !selection.Contains(node.Id)).ToList();
        remaining.Add(new(id, NodeTypes.Subworkflow, 1, new(Parameters: new()
        {
            ["workflow"] = JsonSerializer.SerializeToElement(id), ["revision"] = JsonSerializer.SerializeToElement(1)
        })));
        var edges = graph.Edges.Where(edge => !selection.Contains(edge.Source) && !selection.Contains(edge.Target)).ToList();
        edges.Add(entry with { Target = id, TargetPort = "in" });
        edges.Add(exit with { Source = id, SourcePort = "text" });
        var parentLayout = graph.Layout.Where(pair => !selection.Contains(pair.Key)).ToDictionary();
        parentLayout.Add(id, new(x, y));
        var groups = graph.Groups?.Select(group => group with { Nodes = group.Nodes.Where(nodeId => !selection.Contains(nodeId)).ToArray() })
            .Where(group => group.Nodes.Length > 0).ToList();
        return graph with { Nodes = remaining, Edges = edges, Layout = parentLayout, Groups = groups,
            Subworkflows = [.. graph.Subworkflows ?? [], child] };
    }

    public static bool References(WorkflowNode node, WorkflowDocument child) =>
        node.TypeId == NodeTypes.Subworkflow && node.Config.Parameters is { } parameters &&
        parameters.TryGetValue("workflow", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == child.WorkflowId &&
        (parameters.TryGetValue("revision", out var revision)
            ? revision.ValueKind == JsonValueKind.Number && revision.TryGetInt32(out var number) && number == child.Revision
            : child.Revision == 1);
}
