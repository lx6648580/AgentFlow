using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;

namespace AgentFlow.Editor.POC.Pages;

public partial class Home
{
    [Inject] private HttpClient CatalogHttp { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;

    private NodeCatalog catalog = StandardNodes.Catalog;
    private bool protectStoredSession;
    private readonly Dictionary<(string, string), string> parameterErrors = [];
    private ModelManager modelManager = default!;
    public IReadOnlyList<ModelProfile> ModelProfiles { get; private set; } = [new("mock", "", "mock")];
    public bool ModelProfilesAvailable { get; private set; }
    public IReadOnlyList<McpProfile> McpProfiles { get; private set; } = [];
    public bool McpProfilesAvailable { get; private set; }
    public IEnumerable<string> McpToolOptions => McpProfiles.SelectMany(profile => profile.Tools.Select(tool => $"{profile.Id}/{tool}"));

    public NodeDefinition Definition(WorkflowNode node) => catalog.Get(node.TypeId, node.Version, node.Id);
    public string Label(LocalizedText? label, string fallback) => label?.Get(culture, fallback) ?? fallback;
    public string PortName(CanvasNode node, CanvasPort port, NodeDefinition definition)
    {
        if (node.Definition.TypeId == NodeTypes.Subworkflow &&
            definition.Parameters.FirstOrDefault(field => field.Id == (port.Spec.Output ? "outputName" : "inputName")) is { } field)
        {
            var name = ParameterText(NodeValidator.ReadValue(definition, field, node.Definition.Config));
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return Label(port.Spec.Label, node.Definition.TypeId == NodeTypes.Probe ? PortLabel(port.Id) : port.Id);
    }

    private NodeHelpDialog nodeHelp = default!;
    private static readonly string[] LibraryGroups = ["LibraryInputOutput", "LibraryWorkflow", "LibraryHarness", "LibraryAgents", "LibraryTools", "LibraryData", "LibrarySdk"];

    private static string LibraryGroup(string type) => type switch
    {
        NodeTypes.Input or NodeTypes.Output => "LibraryInputOutput",
        NodeTypes.HarnessAgent => "LibraryHarness",
        _ when NodeTypes.IsHarnessResource(type) => "LibraryHarness",
        _ when !NodeTypes.All.Contains(type) => "LibrarySdk",
        _ when type.StartsWith("ai.", StringComparison.Ordinal) => "LibraryAgents",
        _ when type.StartsWith("tool.", StringComparison.Ordinal) => "LibraryTools",
        _ when type.StartsWith("data.", StringComparison.Ordinal) => "LibraryData",
        _ => "LibraryWorkflow"
    };

    public Task OpenNodeHelpAsync(CanvasNode node) => nodeHelp.OpenAsync(node, Definition(node.Definition));
    private WorkflowDocument ReadWorkflow(string json, bool draft = false) => WorkflowDocument.Read(json, catalog, draft);

    private bool CanExport(WorkflowDocument graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (NodeValidator.Resolve(Definition(node), node.Config).Issues.FirstOrDefault() is not { } issue)
                continue;
            notice = issue.Code;
            return false;
        }
        notice = null;
        return true;
    }

    private async Task LoadCatalogAsync()
    {
        var url = Configuration["NodeCatalogUrl"];
        if (string.IsNullOrWhiteSpace(url))
            return;
        string? json;
        try
        {
            json = await CatalogHttp.GetStringAsync(url);
            catalog = ParseCatalog(json);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or GraphError)
        {
            try
            {
                json = await Js.InvokeAsync<string?>("agentFlow.readCatalog", url);
                if (json is not null)
                    catalog = ParseCatalog(json);
            }
            catch (Exception cacheError) when (cacheError is JSException or JsonException or GraphError)
            {
                notice = "CatalogUnavailable";
            }
            return;
        }
        try { await Js.InvokeVoidAsync("agentFlow.writeCatalog", url, json); }
        catch (JSException error) { Console.Error.WriteLine(error.Message); }
    }

    private static NodeCatalog ParseCatalog(string json)
    {
        var definitions = JsonSerializer.Deserialize<NodeDefinition[]>(json, WorkflowDocument.Json)
            ?? throw new GraphError("InvalidNodeDefinition");
        var result = new NodeCatalog(definitions);
        foreach (var builtin in StandardNodes.Catalog.Definitions)
            result.Get(builtin.TypeId, builtin.Version);
        return result;
    }

    private Task OpenModelManagerAsync() => modelManager.OpenAsync();

    public async Task LoadMcpProfilesAsync()
    {
        if (HostApi.BaseAddress(Configuration["NodeCatalogUrl"]) is not { } host)
            return;
        try
        {
            McpProfiles = await CatalogHttp.GetFromJsonAsync<McpProfile[]>(new Uri(host, "api/tools/mcp-connections")) ?? [];
            McpProfilesAvailable = true;
        }
        catch (Exception error) when (HostApi.IsNetworkError(error)) { McpProfilesAvailable = false; }
        if (document is not null)
            foreach (var node in diagram.Nodes.OfType<CanvasNode>().Where(node => node.Definition.TypeId is NodeTypes.McpTool or NodeTypes.HarnessMcpTools or NodeTypes.Agent))
                node.Refresh();
        StateHasChanged();
    }

    public async Task SelectMcpToolAsync(CanvasNode node, string reference)
    {
        if (!McpToolOptions.Contains(reference, StringComparer.Ordinal))
            return;
        var parts = reference.Split('/');
        var config = node.Definition.Config;
        var parameters = config.Parameters is null ? new Dictionary<string, JsonElement>() : new(config.Parameters);
        parameters["server"] = JsonSerializer.SerializeToElement(parts[0]);
        parameters["tool"] = JsonSerializer.SerializeToElement(parts[1]);
        node.Definition = node.Definition with { Config = config with { Parameters = parameters } };
        await SaveAsync();
        node.Refresh();
    }

    private async Task LoadModelProfilesAsync()
    {
        if (HostApi.BaseAddress(Configuration["NodeCatalogUrl"]) is not { } host)
            return;
        try
        {
            var profiles = await CatalogHttp.GetFromJsonAsync<ModelProfile[]>(new Uri(host, "api/models"))
                ?? throw new JsonException("Missing model profiles.");
            SetModelProfiles(profiles, available: true);
        }
        catch (Exception error) when (HostApi.IsNetworkError(error))
        {
            SetModelProfiles(ModelProfiles, available: false);
        }
    }

    private void UpdateModelProfile(ModelProfile saved) =>
        SetModelProfiles(ModelProfiles.Where(profile => profile.Id != saved.Id).Append(saved).ToArray(), available: true);

    private void SetModelProfiles(IReadOnlyList<ModelProfile> profiles, bool available)
    {
        if (ModelProfilesAvailable == available && ModelProfiles.SequenceEqual(profiles))
            return;
        ModelProfiles = profiles.OrderBy(profile => profile.Id, StringComparer.Ordinal).ToArray();
        ModelProfilesAvailable = available;
        if (document is not null)
        {
            foreach (var node in diagram.Nodes.Cast<CanvasNode>().Where(node => node.Definition.TypeId == NodeTypes.Model))
                node.Refresh();
        }
    }

    public string? ParameterError(CanvasNode node, string id) =>
        parameterErrors.TryGetValue((node.Id, id), out var key) ? T(key) : null;

    public static string ParameterText(JsonElement? element) => element is not { } value ? ""
        : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    public async Task ChangeParameterAsync(CanvasNode node, ParameterDefinition field, object? raw)
    {
        var definition = Definition(node.Definition);
        if (definition.LegacyTextParameter == field.Id)
        {
            await SetText(node, raw?.ToString() ?? "");
            return;
        }
        var text = raw?.ToString() ?? "";
        JsonElement? value = null;
        try
        {
            value = field.Kind switch
            {
                "number" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? JsonSerializer.SerializeToElement(number) : null,
                "toggle" => JsonSerializer.SerializeToElement(raw is true),
                "json" => JsonSerializer.Deserialize<JsonElement>(text),
                _ => JsonSerializer.SerializeToElement(text)
            };
        }
        catch (JsonException)
        {
            parameterErrors[(node.Id, field.Id)] = "ParameterJson";
            return;
        }
        if (value is null)
        {
            parameterErrors[(node.Id, field.Id)] = "ParameterType";
            return;
        }
        var config = node.Definition.Config;
        var parameters = config.Parameters is null ? new Dictionary<string, JsonElement>() : new(config.Parameters);
        parameters[field.Id] = value.Value;
        config = config with { Parameters = parameters };
        if (NodeValidator.Resolve(definition, config).Issues.FirstOrDefault(issue => issue.Field == field.Id) is { } issue)
        {
            parameterErrors[(node.Id, issue.Field)] = issue.Code;
            return;
        }
        parameterErrors.Remove((node.Id, field.Id));
        node.Definition = node.Definition with { Config = config };
        await SaveAsync();
        StateHasChanged();
    }
}
