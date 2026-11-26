using System.Globalization;
using System.Text.Json;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Controls;
using Blazor.Diagrams.Core.Controls.Default;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Blazor.Diagrams.Options;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace AgentFlow.Editor.POC.Pages;

public partial class Home : IDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private BlazorDiagram diagram = default!;
    private WorkflowDocument? document;
    private DotNetObjectReference<Home>? callback;
    private ElementReference canvas;
    private ElementReference searchInput;
    private Point? searchPoint;
    private string search = "";
    private string culture = "en";
    private string preference = "auto";
    private string? notice;
    private bool focusSearch;
    private bool virtualization = true;
    private bool benchmark;
    private int benchmarkIndex;
    private bool viewportSavePending;
    private RunPanel? runPanel;
    private RunCanvasFrame? runFrame;
    private CanvasNode? RunTerminal => diagram.Nodes.OfType<CanvasNode>().FirstOrDefault(node => node.Definition.TypeId == NodeTypes.RunTerminal);
    private WorkflowExamplesDialog examples = default!;
    private bool EditorExercise => WorkflowExamples.All.Any(sample => sample.EditorOnly && sample.Id == document?.WorkflowId);

    public string T(string key) => UiText.Get(key, culture);

    private void SetCanvasFrame(RunCanvasFrame? frame)
    {
        runFrame = frame;
        ApplyCanvasFrame();
    }

    private void ApplyCanvasFrame()
    {
        if (document is null)
            return;
        RunCanvasFrame? frame = null;
        if (!benchmark && runFrame is { } candidate)
        {
            var graph = RunCanvasFrame.GraphKey(Snapshot());
            var path = RunNavigation().Path;
            if (candidate.Graph == graph)
                frame = candidate;
            else if (path.Length > 0 && candidate.Recording is { } recording &&
                RunCanvasFrame.ScopeWorkflow(recording.Definition.Workflow, path) is { } child && RunCanvasFrame.GraphKey(child) == graph)
                frame = RunCanvasFrame.From(graph, recording, candidate.SelectedNode, child, path);
        }
        foreach (var node in diagram.Nodes.Cast<CanvasNode>())
        {
            var state = frame?.Nodes.GetValueOrDefault(node.Id);
            var selected = frame?.SelectedNode == node.Id;
            if (node.RunFrame == state && node.RunId == frame?.RunId && node.Inspected == selected)
                continue;
            node.RunFrame = state;
            node.RunId = frame?.RunId;
            node.Inspected = selected;
            node.Refresh();
        }
    }

    public Task InspectRunNodeAsync(CanvasNode node) => node.RunId is not null && runPanel is { } panel
        ? panel.InspectNodeAsync(node.Id) : Task.CompletedTask;

    private async Task CanvasPointerUpAsync()
    {
        await SaveAsync();
        if (!benchmark && RunTerminal is null && runPanel is { IsInspectorOpen: true } &&
            SelectedNodes() is [{ RunFrame: not null } node] && !node.Inspected)
            await InspectRunNodeAsync(node);
    }

    private (WorkflowTab? Root, string Path) RunNavigation()
    {
        var path = new Stack<string>();
        var tab = activeTab;
        while (tab?.Parent is { } parent && tabs.Find(item => item.Id == parent.TabId) is { } containing)
        {
            path.Push(parent.NodeId);
            tab = containing;
        }
        return (tab, string.Join("/", path));
    }

    private async Task LocateRunNodeAsync(string path)
    {
        if (runFrame?.Recording is null || RunNavigation() is not { Root: { } root } navigation)
            return;
        var currentPath = navigation.Path;
        if (runFrame.Graph == RunCanvasFrame.GraphKey(Snapshot()))
            currentPath = "";
        else if (currentPath.Length == 0 || RunCanvasFrame.GraphKey(ReadWorkflow(root.Draft.Workflow.GetRawText(), draft: true)) != runFrame.Graph)
            return;
        if (currentPath.Length > 0 && path.StartsWith(currentPath + "/", StringComparison.Ordinal))
            path = path[(currentPath.Length + 1)..];
        else if (currentPath.Length > 0)
            await SwitchTabAsync(root);
        while (true)
        {
            var exact = diagram.Nodes.OfType<CanvasNode>().FirstOrDefault(node => node.Id == path);
            if (exact is not null)
            {
                if (exact.Collapsed)
                {
                    exact.Collapsed = false;
                    exact.Refresh();
                }
                FocusNode(exact);
                await SaveAsync();
                return;
            }
            var separator = path.IndexOf('/');
            if (separator < 0 || diagram.Nodes.OfType<CanvasNode>().FirstOrDefault(node =>
                    node.Id == path[..separator] && node.Definition.TypeId == NodeTypes.Subworkflow) is not { } call)
                return;
            var previous = activeTab;
            await OpenSubworkflowAsync(call);
            if (activeTab == previous)
                return;
            path = path[(separator + 1)..];
        }
    }

    public string PortLabel(string id) => id switch
    {
        "in" => T("Input"), "text" => T("Text"), "number" => T("Number"), "value" => T("Value"),
        "model" => T("Model"), "yes" => T("Yes"), "no" => T("No"), "a" => T("BranchA"), "b" => T("BranchB"),
        _ => T("Text") + " " + id[(id.LastIndexOf('-') + 1)..]
    };

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            preference = await Js.InvokeAsync<string>("agentFlow.preference");
            await ApplyLanguageAsync();
            await Task.WhenAll(LoadCatalogAsync(), LoadModelProfilesAsync(), LoadMcpProfilesAsync());
            try
            {
                var json = await Js.InvokeAsync<string?>("agentFlow.readDraft");
                if (json is not null && JsonSerializer.Deserialize<Draft>(json, WorkflowDocument.Json) is { } draft)
                    RestoreSession(draft);
            }
            catch (GraphError error) when (error.Code is "UnknownNodeType" or "UnknownNodeVersion")
            {
                protectStoredSession = true;
                notice = "CatalogDraftError";
            }
            catch (Exception error) when (error is JSException or JsonException or GraphError)
            {
                notice = "DraftError";
            }
            if (document is null)
                OpenBlank();
            StateHasChanged();
        }
        else if (document is not null && callback is null)
        {
            callback = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("agentFlow.bind", canvas, callback);
        }
        if (focusSearch)
        {
            focusSearch = false;
            await searchInput.FocusAsync();
        }
    }

    private void Open(WorkflowDocument graph, Draft? draft = null)
    {
        document = graph;
        openingViewport = draft;
        CloseSearch();
        parameterErrors.Clear();
        groupDefinitions.Clear();
        diagram = new BlazorDiagram(new BlazorDiagramOptions { AllowMultiSelection = true });
        diagram.Options.Virtualization.Enabled = virtualization;
        diagram.Options.Zoom.Minimum = 0.12;
        diagram.Options.Zoom.Maximum = 2.5;
        diagram.UnregisterBehavior<KeyboardShortcutsBehavior>();
        diagram.RegisterComponent<CanvasNode, CanvasNodeView>();
        diagram.ZoomChanged += QueueViewportSave;
        diagram.PointerDoubleClick += (model, e) =>
        {
            if (model is not null)
                return;
            pendingLink = pendingPort = null;
            searchPoint = diagram.GetRelativeMousePoint(e.ClientX, e.ClientY);
            ShowSearch();
            _ = InvokeAsync(StateHasChanged);
        };
        var linkDiagram = diagram;
        var detachedTargets = new Dictionary<BaseLinkModel, SinglePortAnchor>();
        linkDiagram.Links.Added += link =>
        {
            if (link is LinkModel line)
            {
                var resource = link.Source is SinglePortAnchor { Port: CanvasPort source } &&
                    WorkflowDocument.EdgeKind(source.Spec) == "resource";
                line.Color = resource ? "#93bbd3" : "#e7a374";
                line.SelectedColor = resource ? "#c6e1ef" : "#93bbd3";
                line.Width = 2;
                if (!benchmark)
                    linkDiagram.Controls.AddFor(line, ControlsType.OnSelection).Add(new ArrowHeadControl(false));
            }
            link.TargetAttached += RefreshLink;
            link.TargetChanged += TrackTarget;
        };
        linkDiagram.Links.Removed += link =>
        {
            link.TargetAttached -= RefreshLink;
            link.TargetChanged -= TrackTarget;
            linkDiagram.Controls.RemoveFor(link);
            // 库在重连未命中时会删除游离边。恢复已有边，普通删除与新建失败不进入此分支。
            if (detachedTargets.Remove(link, out var target) && link.Source is SinglePortAnchor source &&
                linkDiagram.Nodes.Contains(target.Port.Parent) && linkDiagram.Nodes.Contains(source.Port.Parent))
                linkDiagram.Links.Add(new LinkModel(link.Id, source.Port, target.Port));
        };

        void RefreshLink(BaseLinkModel link)
        {
            notice = null;
            _ = InvokeAsync(StateHasChanged);
        }

        void TrackTarget(BaseLinkModel link, Anchor previous, Anchor next)
        {
            if (link.Target is SinglePortAnchor)
                detachedTargets.Remove(link);
            else if (previous is SinglePortAnchor target)
                detachedTargets.TryAdd(link, target);
        }
        diagram.Batch(() =>
        {
            var nodes = new Dictionary<string, CanvasNode>();
            foreach (var definition in graph.Nodes)
            {
                var point = graph.Layout.GetValueOrDefault(definition.Id) ?? new NodePosition(80 + nodes.Count * 35, 130);
                var node = CreateNode(definition, new Point(point.X, point.Y));
                node.Collapsed = point.Collapsed;
                node.Locked = point.Locked;
                node.Mode = point.Mode;
                node.OutputWidth = point.Width;
                node.OutputHeight = point.Height;
                nodes.Add(node.Id, node);
                diagram.Nodes.Add(node);
            }
            foreach (var edge in graph.Edges)
                diagram.Links.Add(new LinkModel(edge.Id, FindPort(nodes[edge.Source], edge.SourcePort),
                    FindPort(nodes[edge.Target], edge.TargetPort)));
            foreach (var group in graph.Groups ?? [])
                AddGroup(group, group.Nodes.Select(id => nodes[id]).ToArray());
        });
        ApplyCanvasFrame();
        if (draft is not null)
        {
            var opened = diagram;
            opened.ContainerChanged += RestoreViewport;
            void RestoreViewport()
            {
                if (opened.Container is null)
                    return;
                opened.ContainerChanged -= RestoreViewport;
                opened.SetZoom(draft.Zoom);
                opened.SetPan(draft.PanX, draft.PanY);
                openingViewport = null;
            }
        }
    }

    private CanvasNode CreateNode(WorkflowNode definition, Point point)
    {
        var node = new CanvasNode(definition, point);
        foreach (var spec in catalog.Ports(definition))
            node.AddPort(new CanvasPort(node, spec, ValidateConnection));
        return node;
    }

    private static CanvasPort FindPort(CanvasNode node, string id) => (CanvasPort)node.Ports.Single(p => p.Id == id);

    private bool ValidateConnection(CanvasPort from, CanvasPort to)
    {
        try
        {
            var graph = Snapshot();
            graph.ValidateEdge(new("", from.Parent.Id, from.Id, to.Parent.Id, to.Id, WorkflowDocument.EdgeKind(from.Spec)), graph.Edges, catalog);
            return true;
        }
        catch (GraphError error)
        {
            notice = error.Code;
            _ = InvokeAsync(StateHasChanged);
            return false;
        }
    }

    private WorkflowDocument Snapshot()
    {
        var nodes = diagram.Nodes.Cast<CanvasNode>().ToList();
        var edges = new List<WorkflowEdge>();
        foreach (var link in diagram.Links)
        {
            if (link.Source is SinglePortAnchor { Port: CanvasPort source } &&
                link.Target is SinglePortAnchor { Port: CanvasPort target })
                edges.Add(new(link.Id, source.Parent.Id, source.Id, target.Parent.Id, target.Id, WorkflowDocument.EdgeKind(source.Spec)));
        }
        return document! with
        {
            Nodes = nodes.Select(node => node.Definition).ToList(),
            Edges = edges,
            Layout = nodes.ToDictionary(node => node.Id, node => new NodePosition(node.Position.X, node.Position.Y,
                node.Collapsed, node.Locked, node.Mode) { Width = node.OutputWidth, Height = node.OutputHeight }),
            Groups = groupDefinitions.Count == 0 ? null : groupDefinitions.Select(item => item.Value with
            {
                Nodes = item.Key.Children.Select(node => node.Id).ToArray()
            }).ToList()
        };
    }

    internal NodeDefinition? ExampleNode(WorkflowNode node) =>
        catalog.Definitions.FirstOrDefault(entry => entry.TypeId == node.TypeId && entry.Version == node.Version);

    internal async Task LoadExampleAsync(WorkflowExample example, WorkflowDocument definition)
    {
        var graph = ReadWorkflow(EncodeGraph(definition).GetRawText());
        if (!example.EditorOnly)
            graph = WithRunTerminal(graph);
        notice = null;
        OpenTab(graph, T(example.Title), saved: true, FitInitialViewport(graph));
        StateHasChanged();
        await SaveAsync(capture: false);
    }

    private Draft? FitInitialViewport(WorkflowDocument graph)
    {
        if (diagram.Container is not { Width: > 0, Height: > 0 } view || graph.Nodes.Count == 0)
            return null;
        var bounds = graph.Nodes.Select((node, index) =>
        {
            var point = graph.Layout.GetValueOrDefault(node.Id) ?? new NodePosition(80 + index * 35, 130);
            return (point.X, point.Y, Width: point.Width ?? (node.TypeId == NodeTypes.RunTerminal ? 485 : 230),
                Height: point.Height ?? 175);
        }).ToArray();
        var left = bounds.Min(point => point.X);
        var top = bounds.Min(point => point.Y);
        var right = bounds.Max(point => point.X + point.Width);
        var bottom = bounds.Max(point => point.Y + point.Height);
        var zoom = Math.Clamp(Math.Min((view.Width - 64) / (right - left), (view.Height - 64) / (bottom - top)), 0.12, 1);
        return new(default, 32 - left * zoom, 32 - top * zoom, zoom, []);
    }

    private async Task ImportAsync(InputFileChangeEventArgs args)
    {
        try
        {
            using var reader = new StreamReader(args.File.OpenReadStream(2 * 1024 * 1024));
            var graph = ReadWorkflow(await reader.ReadToEndAsync());
            notice = null;
            OpenTab(graph, args.File.Name.Replace(".workflow.json", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".json", "", StringComparison.OrdinalIgnoreCase), saved: true);
            await SaveAsync(capture: false);
        }
        catch (GraphError error) { notice = error.Code; }
        catch (Exception error) when (error is IOException or JsonException) { notice = "InvalidDocument"; }
    }

    private async Task ExportAsync()
    {
        var graph = Snapshot();
        if (!CanExport(graph))
            return;
        await Js.InvokeVoidAsync("agentFlow.download", "workflow.json", JsonSerializer.Serialize(graph, WorkflowDocument.Json));
    }

    private async Task ApplyLanguageAsync()
    {
        culture = preference is "en" or "zh-Hans" or "zh-Hant"
            ? preference : UiText.Detect(await Js.InvokeAsync<string[]>("agentFlow.languages"));
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        await Js.InvokeVoidAsync("agentFlow.setLanguage", culture);
    }

    private async Task ChangeLanguageAsync(ChangeEventArgs args)
    {
        preference = args.Value?.ToString() ?? "auto";
        notice = null;
        await SaveAsync();
        if (!await Js.InvokeAsync<bool>("agentFlow.writePreference", preference))
            notice ??= "PreferenceError";
        await ApplyLanguageAsync();
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        foreach (var node in diagram.Nodes)
            node.RefreshAll();
        foreach (var (group, definition) in groupDefinitions)
        {
            group.Title = definition.Title ?? T("Group");
            group.Refresh();
        }
    }

    private async Task SaveAsync(bool record = true, bool capture = true)
    {
        if (activeTab is null || benchmark)
            return;
        if (capture)
            CaptureActive(record);
        if (runFrame is not null && capture)
            ApplyCanvasFrame();
        if (protectStoredSession)
            return;
        var draft = activeTab.Draft with { Session = new(activeTab.Id, tabs) };
        try
        {
            await Js.InvokeVoidAsync("agentFlow.writeDraft", JsonSerializer.Serialize(draft, SessionJson));
            if (notice == "DraftError")
            {
                notice = null;
                StateHasChanged();
            }
        }
        catch (JSException)
        {
            notice = "DraftError";
            StateHasChanged();
        }
    }

    private void QueueViewportSave()
    {
        if (benchmark || viewportSavePending)
            return;
        viewportSavePending = true;
        _ = InvokeAsync(async () =>
        {
            // 滚轮缩放时画布会同时调整平移，等这一轮变更结束再取快照。
            await Task.Yield();
            viewportSavePending = false;
            await SaveAsync();
            StateHasChanged();
        });
    }

    [JSInvokable] public void SelectRectangle(string[] ids, bool append)
    {
        if (!append)
            diagram.UnselectAll();
        foreach (var node in diagram.Nodes.Where(node => ids.Contains(node.Id)))
            diagram.SelectModel(node, false);
        StateHasChanged();
    }

    public async Task SetText(CanvasNode node, string text)
    {
        node.Definition = node.Definition with { Config = node.Definition.Config with { Text = text } };
        await SaveAsync();
        StateHasChanged();
    }

    public async Task Toggle(CanvasNode node)
    {
        node.Collapsed = !node.Collapsed;
        node.ReinitializePorts();
        node.RefreshAll();
        await SaveAsync();
    }

    public async Task ChangePorts(CanvasNode node, int delta)
    {
        var count = Math.Clamp(node.Definition.Config.Ports + delta, 0, 16);
        if (count == node.Definition.Config.Ports)
            return;
        node.Definition = node.Definition with { Config = node.Definition.Config with { Ports = count } };
        if (delta > 0)
            node.AddPort(new CanvasPort(node, catalog.Ports(node.Definition).Last(), ValidateConnection));
        else
        {
            var port = FindPort(node, $"text-{count + 1}");
            foreach (var link in port.Links.ToArray())
                diagram.Links.Remove(link);
            node.RemovePort(port);
        }
        node.ReinitializePorts();
        node.RefreshAll();
        await SaveAsync();
        StateHasChanged();
    }

    private void AddAtCenter()
    {
        searchPoint = diagram.GetRelativePoint(285, 150);
        pendingLink = pendingPort = null;
        ShowSearch();
    }

    private void ShowSearch()
    {
        contextMenu = false;
        search = "";
        searchIndex = 0;
        focusSearch = true;
    }

    private void CloseSearch()
    {
        searchPoint = null;
        pendingLink = pendingPort = null;
        contextMenu = false;
        focusSearch = false;
    }

    private void ChangeSearch(ChangeEventArgs args)
    {
        search = args.Value?.ToString() ?? "";
        searchIndex = 0;
    }

    private NodeDefinition[] MatchingTypes() => catalog.Latest.Where(definition => IsCompatibleType(definition) &&
        (Label(definition.Name, definition.TypeId).Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        (definition.Name?.En ?? definition.TypeId).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();

    private async Task SearchKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            CloseSearch();
        var matches = MatchingTypes();
        if (matches.Length == 0)
            return;
        if (e.Key == "ArrowDown")
            searchIndex = (searchIndex + 1) % matches.Length;
        if (e.Key == "ArrowUp")
            searchIndex = (searchIndex + matches.Length - 1) % matches.Length;
        if (e.Key == "Enter")
            await AddAsync(matches[searchIndex].TypeId);
    }

    private async Task AddAsync(string type)
    {
        if (type == NodeTypes.RunTerminal && RunTerminal is not null)
        {
            CloseSearch();
            await FocusRunTerminalAsync();
            return;
        }
        var point = searchPoint ?? diagram.GetRelativePoint(285, 150);
        var definition = catalog.GetLatest(type);
        var config = new NodeConfig(Ports: type == NodeTypes.Probe ? 1 : 0);
        var defaults = NodeValidator.Resolve(definition, config).Values;
        if (defaults.Count > 0)
            config = config with { Parameters = new(defaults) };
        var node = new WorkflowNode(Guid.NewGuid().ToString("N"), type, definition.Version, config);
        var added = diagram.Nodes.Add(CreateNode(node, point));
        if (pendingLink is not null && diagram.Nodes.FirstOrDefault(item => item.Id == pendingLink) is CanvasNode source)
        {
            var output = FindPort(source, pendingPort!);
            var input = added.Ports.Cast<CanvasPort>().First(port => !port.Spec.Output && WorkflowDocument.PortsMatch(output.Spec, port.Spec));
            diagram.Links.Add(new LinkModel(output, input));
        }
        CloseSearch();
        await SaveAsync();
        await canvas.FocusAsync();
    }

    public async Task FocusRunTerminalAsync()
    {
        if (RunTerminal is not { } terminal)
            return;
        if (terminal.Collapsed)
        {
            terminal.Collapsed = false;
            terminal.Refresh();
        }
        FocusNode(terminal);
        await SaveAsync();
    }

    private void FocusNode(CanvasNode node)
    {
        if (diagram.Container is not { } view)
            return;
        var zoom = diagram.Zoom;
        var width = node.Size?.Width ?? (node.Definition.TypeId == NodeTypes.RunTerminal ? 480 : 223);
        var height = node.Size?.Height ?? 160;
        var x = diagram.Pan.X + node.Position.X * zoom;
        var y = diagram.Pan.Y + node.Position.Y * zoom;
        if (x >= 8 && y >= 8 && x + width * zoom <= view.Width - 8 && y + height * zoom <= view.Height - 8)
            return;
        diagram.SetPan(view.Width / 2 - (node.Position.X + width / 2) * zoom,
            view.Height / 2 - (node.Position.Y + height / 2) * zoom);
    }

    [JSInvokable]
    public async Task ResizeOutput(string id, double width, double height)
    {
        if (diagram.Nodes.FirstOrDefault(item => item.Id == id) is not CanvasNode { Definition.TypeId: NodeTypes.Output, Locked: false, Collapsed: false } node ||
            !double.IsFinite(width) || !double.IsFinite(height))
            return;
        node.OutputWidth = Math.Round(Math.Clamp(width, 223, 1600));
        node.OutputHeight = Math.Round(Math.Clamp(height, 130, 1200));
        node.Refresh();
        await SaveAsync();
    }

    private Task CanvasKeyAsync(KeyboardEventArgs e) => e.Key == "Delete" && searchPoint is null
        ? DeleteSelectionAsync() : Task.CompletedTask;

    private async Task DeleteSelectionAsync()
    {
        diagram.Batch(() =>
        {
            foreach (var model in diagram.GetSelectedModels().ToArray())
            {
                if (model is BaseLinkModel link)
                    diagram.Links.Remove(link);
                if (model is GroupModel group)
                    RemoveGroup(group);
                if (model is CanvasNode node)
                {
                    if (node.Group is { } parent)
                    {
                        parent.RemoveChild(node);
                        if (parent.Children.Count == 0)
                            RemoveGroup(parent);
                    }
                    diagram.Nodes.Remove(node);
                }
            }
        });
        await SaveAsync();
    }

    private void SetVirtualization(ChangeEventArgs e)
    {
        virtualization = e.Value is true;
        var graph = Snapshot();
        var draft = Capture();
        Open(graph, draft);
    }

    private async Task Fit()
    {
        diagram.ZoomToFit(55);
        await SaveAsync();
    }

    private void LoadBenchmark(int count, int links)
    {
        CaptureActive();
        benchmark = true;
        benchmarkIndex = 0;
        Open(BenchmarkGraph.Create(count, links));
    }

    [JSInvokable] public void UpdateBenchmark()
    {
        if (!benchmark)
            return;
        var nodes = diagram.Nodes.Cast<CanvasNode>().ToArray();
        for (var update = 0; update < 10; update++)
        {
            var node = nodes[benchmarkIndex++ % nodes.Length];
            node.Active = !node.Active;
            node.Refresh();
        }
    }

    public void Dispose() => callback?.Dispose();

}
