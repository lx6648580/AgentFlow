using System.Text.Json;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components.Web;

namespace AgentFlow.Editor.POC.Pages;

public partial class Home
{
    // ponytail: 小图用完整快照便于恢复，单标签保留最近 30 步。大图时再换成增量命令。
    private const int HistoryLimit = 30;
    private static readonly JsonSerializerOptions SessionJson = new(WorkflowDocument.Json) { WriteIndented = false };
    private readonly List<WorkflowTab> tabs = [];
    private readonly Dictionary<GroupModel, WorkflowGroup> groupDefinitions = [];
    private WorkflowTab? activeTab;
    private WorkflowDocument? clipboard;
    private WorkflowTab? pendingClose;
    private string? pendingLink;
    private string? pendingPort;
    private bool contextMenu;
    private int searchIndex;
    private bool showMinimap = true;
    private Draft? openingViewport;

    private sealed record Draft(JsonElement Workflow, double PanX, double PanY, double Zoom, string[] Collapsed,
        Session? Session = null);
    private sealed record Session(string ActiveId, List<WorkflowTab> Tabs);

    private sealed class WorkflowTab
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public required string Name { get; set; }
        public required Draft Draft { get; set; }
        public string? Saved { get; set; }
        public SubflowTabLink? Parent { get; set; }
        public List<JsonElement> Undo { get; init; } = [];
        public List<JsonElement> Redo { get; init; } = [];
        public bool Dirty => Saved != Draft.Workflow.GetRawText();
    }

    private static JsonElement EncodeGraph(WorkflowDocument graph) => JsonSerializer.SerializeToElement(graph, SessionJson);

    private Draft Capture() => new(EncodeGraph(Snapshot()),
        openingViewport?.PanX ?? diagram.Pan.X, openingViewport?.PanY ?? diagram.Pan.Y,
        openingViewport?.Zoom ?? diagram.Zoom, []);

    private void CaptureActive(bool record = true)
    {
        if (activeTab is null || benchmark)
            return;
        var next = Capture();
        if (record && next.Workflow.GetRawText() != activeTab.Draft.Workflow.GetRawText())
        {
            Push(activeTab.Undo, activeTab.Draft.Workflow);
            activeTab.Redo.Clear();
        }
        activeTab.Draft = next;
    }

    private void RestoreSession(Draft stored)
    {
        if (stored.Session is { Tabs.Count: > 0 } session)
        {
            foreach (var tab in session.Tabs)
            {
                tab.Draft = tab.Draft with { Workflow = EncodeGraph(ReadWorkflow(tab.Draft.Workflow.GetRawText(), draft: true)) };
                if (tab.Saved is not null)
                    tab.Saved = EncodeGraph(ReadWorkflow(tab.Saved)).GetRawText();
            }
            tabs.AddRange(session.Tabs);
            Activate(tabs.FirstOrDefault(tab => tab.Id == session.ActiveId) ?? tabs[0]);
            return;
        }
        var graph = ReadWorkflow(stored.Workflow.GetRawText(), draft: true);
        // 迁移 Phase 00 单草稿，折叠状态今后由 JSON 布局保存。
        var layout = graph.Layout.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var id in stored.Collapsed ?? [])
        {
            if (layout.TryGetValue(id, out var position))
                layout[id] = position with { Collapsed = true };
        }
        OpenTab(graph with { Layout = layout }, graph.WorkflowId, saved: false, stored);
    }

    private void OpenTab(WorkflowDocument graph, string name, bool saved, Draft? view = null, SubflowTabLink? parent = null)
    {
        CaptureActive();
        var data = EncodeGraph(graph);
        var tab = new WorkflowTab
        {
            Name = name,
            Parent = parent,
            Draft = new(data, view?.PanX ?? 0, view?.PanY ?? 0, view?.Zoom ?? 1, [])
        };
        tabs.Add(tab);
        Activate(tab);
        // 以画布规范化后的快照作为初始内容，避免布局字段顺序或默认值产生未保存标记。
        tab.Draft = Capture();
        if (saved)
            tab.Saved = tab.Draft.Workflow.GetRawText();
    }

    private void Activate(WorkflowTab tab)
    {
        activeTab = tab;
        benchmark = false;
        Open(ReadWorkflow(tab.Draft.Workflow.GetRawText(), draft: true), tab.Draft);
    }

    private async Task SwitchTabAsync(WorkflowTab tab)
    {
        if (tab == activeTab && !benchmark)
            return;
        CaptureActive();
        Activate(tab);
        await SaveAsync(capture: false);
    }

    private void OpenBlank()
    {
        var id = Guid.NewGuid().ToString("N");
        OpenTab(WithRunTerminal(new WorkflowDocument("0.1", id, 1, [], [], [])), T("Untitled"), saved: false);
    }

    private static WorkflowDocument WithRunTerminal(WorkflowDocument graph)
    {
        if (graph.Nodes.Any(node => node.TypeId == NodeTypes.RunTerminal))
            return graph;
        var id = Guid.NewGuid().ToString("N");
        return graph with
        {
            Nodes = [new(id, NodeTypes.RunTerminal, 1, new()), .. graph.Nodes],
            Layout = new(graph.Layout) { [id] = new(54, graph.Layout.Values.Select(item => item.Y).DefaultIfEmpty(705).Min() - 370) }
        };
    }

    private async Task NewAsync()
    {
        OpenBlank();
        await SaveAsync();
    }

    private async Task RequestCloseAsync(WorkflowTab tab)
    {
        CaptureActive();
        if (tab.Dirty)
            pendingClose = tab;
        else
            await CloseTabAsync(tab);
    }

    private async Task CloseTabAsync(WorkflowTab tab)
    {
        pendingClose = null;
        var index = tabs.IndexOf(tab);
        tabs.Remove(tab);
        if (tab == activeTab)
        {
            activeTab = null;
            if (tabs.Count == 0)
                OpenBlank();
            else
                Activate(tabs[Math.Min(index, tabs.Count - 1)]);
        }
        await SaveAsync(capture: false);
    }

    private async Task SaveFileAsync(bool saveAs = false)
    {
        if (activeTab is null || benchmark)
            return;
        CaptureActive();
        var tab = activeTab;
        var graph = Snapshot();
        if (!CanExport(graph))
            return;
        var name = saveAs
            ? await Js.InvokeAsync<string?>("agentFlow.askName", T("SaveAs"), tab.Name)
            : tab.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Replace('\\', '/').Split('/').Last().Trim().TrimEnd('.');
        if (name.EndsWith(".workflow.json", StringComparison.OrdinalIgnoreCase))
            name = name[..^14];
        else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        if (string.IsNullOrWhiteSpace(name))
            name = T("Untitled");
        if (tab.Dirty && tab.Saved is not null)
            graph = graph with { Revision = graph.Revision + 1 };
        document = graph;
        var data = EncodeGraph(graph);
        await Js.InvokeVoidAsync("agentFlow.download", $"{name}.workflow.json", JsonSerializer.Serialize(graph, WorkflowDocument.Json));
        tab.Name = name;
        tab.Saved = data.GetRawText();
        await SaveAsync(record: false);
    }

    private static void Push(List<JsonElement> stack, JsonElement graph)
    {
        if (stack.Count == HistoryLimit)
            stack.RemoveAt(0);
        stack.Add(graph);
    }

    private async Task TravelAsync(bool redo)
    {
        CaptureActive();
        if (activeTab is null || benchmark)
            return;
        var source = redo ? activeTab.Redo : activeTab.Undo;
        var target = redo ? activeTab.Undo : activeTab.Redo;
        if (source.Count == 0)
            return;
        Push(target, activeTab.Draft.Workflow);
        var previous = source[^1];
        source.RemoveAt(source.Count - 1);
        var graph = ReadWorkflow(previous.GetRawText(), draft: true);
        // 撤销只修改编辑内容，已保存的修订号保持单调。
        graph = graph with { Revision = document!.Revision };
        activeTab.Draft = activeTab.Draft with { Workflow = EncodeGraph(graph) };
        Open(graph, activeTab.Draft);
        await SaveAsync(capture: false);
    }

    private CanvasNode[] SelectedNodes() => diagram.GetSelectedModels().OfType<CanvasNode>().ToArray();

    private void CopySelection()
    {
        var ids = SelectedNodes().Select(node => node.Id).ToHashSet();
        foreach (var group in diagram.GetSelectedModels().OfType<GroupModel>())
            ids.UnionWith(group.Children.Select(node => node.Id));
        ids.ExceptWith(diagram.Nodes.OfType<CanvasNode>().Where(node => node.Definition.TypeId == NodeTypes.RunTerminal).Select(node => node.Id));
        if (ids.Count == 0)
            return;
        var graph = Snapshot();
        clipboard = graph with
        {
            Nodes = graph.Nodes.Where(node => ids.Contains(node.Id)).ToList(),
            Edges = graph.Edges.Where(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target)).ToList(),
            Layout = graph.Layout.Where(item => ids.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value),
            Groups = graph.Groups?.Where(group => group.Nodes.All(ids.Contains)).ToList(),
            Subworkflows = graph.Subworkflows?.Where(child => graph.Nodes.Any(node => ids.Contains(node.Id) &&
                SubworkflowEditing.References(node, child))).ToList()
        };
    }

    private async Task PasteAsync()
    {
        if (clipboard is null)
            return;
        var graph = Snapshot();
        var view = Capture();
        var children = (graph.Subworkflows ?? []).ToList();
        foreach (var child in clipboard.Subworkflows ?? [])
        {
            var existing = children.Find(item => item.WorkflowId == child.WorkflowId && item.Revision == child.Revision);
            if (existing is null)
                children.Add(child);
            else if (EncodeGraph(existing).GetRawText() != EncodeGraph(child).GetRawText())
            {
                notice = "SubworkflowVersionConflict";
                return;
            }
        }
        var ids = clipboard.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid().ToString("N"));
        var point = diagram.GetRelativePoint(300, 170);
        var left = clipboard.Layout.Values.Min(position => position.X);
        var top = clipboard.Layout.Values.Min(position => position.Y);
        graph.Nodes.AddRange(clipboard.Nodes.Select(node => node with { Id = ids[node.Id] }));
        graph.Edges.AddRange(clipboard.Edges.Select(edge => edge with
        {
            Id = Guid.NewGuid().ToString("N"), Source = ids[edge.Source], Target = ids[edge.Target]
        }));
        foreach (var (id, position) in clipboard.Layout)
            graph.Layout.Add(ids[id], position with { X = point.X + position.X - left, Y = point.Y + position.Y - top });
        graph = graph with
        {
            Subworkflows = children.Count == 0 ? null : children,
            Groups = [..graph.Groups ?? [], ..(clipboard.Groups ?? []).Select(group => group with
            {
                Id = Guid.NewGuid().ToString("N"), Nodes = group.Nodes.Select(id => ids[id]).ToArray()
            })]
        };
        Open(graph, view);
        foreach (var node in diagram.Nodes.Where(node => ids.ContainsValue(node.Id)))
            diagram.SelectModel(node, false);
        await SaveAsync();
    }

    private void AddGroup(WorkflowGroup definition, CanvasNode[] nodes)
    {
        var group = diagram.Groups.Group(nodes);
        group.Title = definition.Title ?? T("Group");
        group.Locked = nodes.Any(node => node.Locked);
        groupDefinitions.Add(group, definition);
    }

    private async Task GroupSelectionAsync()
    {
        var nodes = SelectedNodes().Where(node => node.Group is null).ToArray();
        if (nodes.Length < 2)
            return;
        AddGroup(new(Guid.NewGuid().ToString("N"), null, nodes.Select(node => node.Id).ToArray()), nodes);
        await SaveAsync();
    }

    private void RemoveGroup(GroupModel group)
    {
        group.Ungroup();
        diagram.Groups.Remove(group);
        groupDefinitions.Remove(group);
    }

    private async Task UngroupSelectionAsync()
    {
        var groups = diagram.GetSelectedModels().OfType<GroupModel>()
            .Concat(SelectedNodes().Select(node => node.Group).OfType<GroupModel>()).Distinct().ToArray();
        foreach (var group in groups)
            RemoveGroup(group);
        await SaveAsync();
    }

    private async Task LockSelectionAsync(bool value)
    {
        foreach (var node in SelectedNodes())
        {
            node.Locked = value;
            node.Refresh();
            if (node.Group is { } group)
                group.Locked = group.Children.Any(child => child.Locked);
        }
        await SaveAsync();
    }

    private async Task ModeSelectionAsync(string? mode)
    {
        foreach (var node in SelectedNodes())
        {
            node.Mode = node.Mode == mode ? null : mode;
            node.Refresh();
        }
        await SaveAsync();
    }

    [JSInvokable]
    public async Task Shortcut(string command)
    {
        switch (command)
        {
            case "undo": await TravelAsync(false); break;
            case "redo": await TravelAsync(true); break;
            case "copy": CopySelection(); break;
            case "paste": await PasteAsync(); break;
            case "group": await GroupSelectionAsync(); break;
            case "save": await SaveFileAsync(); break;
            case "new": await NewAsync(); break;
            case "bypass": await ModeSelectionAsync("bypass"); break;
            case "disabled": await ModeSelectionAsync("disabled"); break;
        }
        StateHasChanged();
    }

    [JSInvokable]
    public void OpenContext(double x, double y, string? selectedNode)
    {
        if (selectedNode is not null)
        {
            var selected = diagram.Nodes.Cast<Blazor.Diagrams.Core.Models.Base.SelectableModel>()
                .Concat(diagram.Links).Concat(diagram.Groups).FirstOrDefault(model => model.Id == selectedNode);
            if (selected is { Selected: false })
                diagram.SelectModel(selected, true);
        }
        searchPoint = diagram.GetRelativeMousePoint(x, y);
        pendingLink = pendingPort = null;
        contextMenu = true;
        focusSearch = false;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task BrowserLanguageChanged()
    {
        if (preference == "auto")
        {
            await ApplyLanguageAsync();
            RefreshLabels();
            StateHasChanged();
        }
    }

    [JSInvokable]
    public void OpenDownstream(double x, double y, string source, string port)
    {
        if (diagram.Nodes.FirstOrDefault(node => node.Id == source) is not CanvasNode node ||
            !catalog.Ports(node.Definition).Any(spec => spec.Id == port && spec.Output))
            return;
        searchPoint = diagram.GetRelativeMousePoint(x, y);
        pendingLink = source;
        pendingPort = port;
        ShowSearch();
        StateHasChanged();
    }

    private bool IsCompatibleType(NodeDefinition definition)
    {
        if (pendingLink is null)
            return true;
        if (diagram.Nodes.FirstOrDefault(node => node.Id == pendingLink) is not CanvasNode node)
            return false;
        var source = FindPort(node, pendingPort!);
        return definition.Ports.Any(port => !port.Output && WorkflowDocument.PortsMatch(source.Spec, port));
    }

    private async Task AddFromLibraryAsync(string type)
    {
        CloseSearch();
        // 连续从库添加时错开位置，避免同一坐标上的节点彼此遮挡。
        var index = diagram.Nodes.Count;
        Point point;
        do
        {
            point = diagram.GetRelativePoint(85 + index % 4 * 255, 90 + index / 4 * 190);
            index++;
        } while (diagram.Nodes.Any(node => Math.Abs(node.Position.X - point.X) < 235 &&
            Math.Abs(node.Position.Y - point.Y) < 155) && index < diagram.Nodes.Count + 30);
        searchPoint = point;
        await AddAsync(type);
    }

    [JSInvokable]
    public async Task DropFromLibrary(string type, double x, double y)
    {
        if (!catalog.Latest.Any(definition => definition.TypeId == type && definition.ShowInLibrary))
            return;
        CloseSearch();
        searchPoint = diagram.GetRelativeMousePoint(x, y);
        await AddAsync(type);
        StateHasChanged();
    }

    private const double MapWidth = 164;
    private const double MapHeight = 100;

    private (double X, double Y, double Width, double Height) MapBounds()
    {
        var points = diagram.Nodes.Select(node => node.Position).ToArray();
        return points.Length == 0 ? (0, 0, 1, 1)
            : (points.Min(p => p.X) - 30, points.Min(p => p.Y) - 30,
                points.Max(p => p.X) - points.Min(p => p.X) + 300,
                points.Max(p => p.Y) - points.Min(p => p.Y) + 220);
    }

    private IEnumerable<(string Kind, string Style)> MapMarks()
    {
        var bounds = MapBounds();
        string Position(double x, double y, double width, double height) => FormattableString.Invariant(
            $"left:{(x - bounds.X) / bounds.Width * 100:G5}%;top:{(y - bounds.Y) / bounds.Height * 100:G5}%;width:{width / bounds.Width * 100:G5}%;height:{height / bounds.Height * 100:G5}%");
        foreach (var node in diagram.Nodes)
            yield return ("map-node", Position(node.Position.X, node.Position.Y, 223, 85));
        if (diagram.Container is { } view)
            yield return ("map-viewport", Position(-diagram.Pan.X / diagram.Zoom, -diagram.Pan.Y / diagram.Zoom,
                view.Width / diagram.Zoom, view.Height / diagram.Zoom));
    }

    private async Task MoveFromMinimapAsync(MouseEventArgs e)
    {
        if (diagram.Container is not { } view)
            return;
        var bounds = MapBounds();
        var x = bounds.X + e.OffsetX / MapWidth * bounds.Width;
        var y = bounds.Y + e.OffsetY / MapHeight * bounds.Height;
        diagram.SetPan(view.Width / 2 - x * diagram.Zoom, view.Height / 2 - y * diagram.Zoom);
        await SaveAsync();
    }
}
