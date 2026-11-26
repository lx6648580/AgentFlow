using System.Text.Json;
using Blazor.Diagrams.Core.Models;

namespace AgentFlow.Editor.POC.Pages;

public partial class Home
{
    private sealed record SubflowTabLink(string TabId, string NodeId, int Revision);

    private async Task ExtractSubworkflowAsync()
    {
        CloseSearch();
        var selection = SelectedNodes().Select(node => node.Id).ToHashSet();
        foreach (var group in diagram.GetSelectedModels().OfType<GroupModel>())
            selection.UnionWith(group.Children.Select(node => node.Id));
        try
        {
            var view = Capture();
            var graph = SubworkflowEditing.Encapsulate(Snapshot(), selection, "sub-" + Guid.NewGuid().ToString("N"));
            Open(ReadWorkflow(EncodeGraph(graph).GetRawText(), draft: true), view);
            await SaveAsync();
        }
        catch (GraphError error) { notice = error.Code; }
    }

    public async Task OpenSubworkflowAsync(CanvasNode node)
    {
        if (node.Definition.TypeId != NodeTypes.Subworkflow || activeTab is null)
            return;
        var child = document?.Subworkflows?.Find(item => SubworkflowEditing.References(node.Definition, item));
        if (child is null)
        {
            notice = "SubworkflowNotFound";
            StateHasChanged();
            return;
        }
        var parent = new SubflowTabLink(activeTab.Id, node.Id, child.Revision);
        if (tabs.Find(tab => tab.Parent == parent) is { } opened)
            await SwitchTabAsync(opened);
        else
        {
            var graph = RunTerminal is not null ? WithRunTerminal(child) : child;
            OpenTab(graph, $"{child.WorkflowId} · r{child.Revision}", saved: true, FitInitialViewport(graph), parent);
            await SaveAsync(capture: false);
        }
        StateHasChanged();
    }

    private async Task PublishSubworkflowAsync()
    {
        if (activeTab is not { Parent: { } link } childTab)
            return;
        CaptureActive();
        var child = Snapshot();
        if (!CanExport(child))
            return;
        var parent = tabs.Find(tab => tab.Id == link.TabId);
        var graph = parent is null ? null : ReadWorkflow(parent.Draft.Workflow.GetRawText(), draft: true);
        var call = graph?.Nodes.Find(node => node.Id == link.NodeId);
        if (graph?.Subworkflows is not { } versions || call is null ||
            !SubworkflowEditing.References(call, child with { Revision = link.Revision }) ||
            !versions.Any(item => item.WorkflowId == child.WorkflowId && item.Revision == link.Revision))
        {
            notice = "SubworkflowVersionConflict";
            return;
        }

        var latest = versions.Where(item => item.WorkflowId == child.WorkflowId).Max(item => item.Revision);
        if (latest == int.MaxValue)
        {
            notice = "SubworkflowVersionConflict";
            return;
        }
        child = child with { Revision = latest + 1 };
        var parameters = new Dictionary<string, JsonElement>(call.Config.Parameters!) { ["revision"] = JsonSerializer.SerializeToElement(child.Revision) };
        graph.Nodes[graph.Nodes.IndexOf(call)] = call with { Config = call.Config with { Parameters = parameters } };
        graph = graph with { Subworkflows = [.. versions, child] };
        var next = EncodeGraph(graph);
        try { _ = ReadWorkflow(next.GetRawText(), draft: true); }
        catch (GraphError error) { notice = error.Code; return; }

        Push(parent!.Undo, parent.Draft.Workflow);
        parent.Redo.Clear();
        parent.Draft = parent.Draft with { Workflow = next };
        childTab.Draft = childTab.Draft with { Workflow = EncodeGraph(child) };
        childTab.Saved = childTab.Draft.Workflow.GetRawText();
        childTab.Name = $"{child.WorkflowId} · r{child.Revision}";
        childTab.Parent = link with { Revision = child.Revision };
        Activate(parent);
        await SaveAsync(capture: false);
    }
}
