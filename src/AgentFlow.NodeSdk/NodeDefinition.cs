using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFlow;

public sealed record LocalizedText(string? En = null, string? Hans = null, string? Hant = null)
{
    public string Get(string culture, string fallback)
    {
        var localized = culture switch { "zh-Hans" => Hans, "zh-Hant" => Hant, _ => En };
        if (!string.IsNullOrWhiteSpace(localized))
            return localized;
        return string.IsNullOrWhiteSpace(En) ? fallback : En;
    }
}

public sealed record PortDefinition(string Id, string Type, bool Output = false, bool Required = false,
    int? MaxConnections = null, LocalizedText? Label = null, LocalizedText? Help = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceKind { get; init; }
}

public sealed record ParameterDefinition(string Id, string Kind, LocalizedText? Label = null,
    bool Required = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Default = default,
    decimal? Min = null, decimal? Max = null,
    bool Integer = false, IReadOnlyList<string>? Choices = null, LocalizedText? Help = null,
    IReadOnlyDictionary<string, LocalizedText>? ChoiceLabels = null);

public sealed record NodeDefinition(string TypeId, int Version, LocalizedText? Name,
    IReadOnlyList<PortDefinition> Ports, IReadOnlyList<ParameterDefinition> Parameters,
    LocalizedText? Help = null, bool ShowInLibrary = true, string? LegacyTextParameter = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeDocumentation? Documentation { get; init; }
}

public sealed record NodeDocumentation(LocalizedText Usage, LocalizedText MafMapping, string? ReferenceUrl = null);

public sealed class NodeCatalog
{
    private readonly Dictionary<(string, int), NodeDefinition> entries = [];
    private readonly Dictionary<string, NodeDefinition> latest = new(StringComparer.Ordinal);

    public NodeCatalog(IEnumerable<NodeDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (definition is null)
                throw new GraphError("InvalidNodeDefinition");
            CheckDefinition(definition);
            if (!entries.TryAdd((definition.TypeId, definition.Version), definition))
                throw new GraphError("DuplicateNodeVersion", definition.TypeId);
            if (!latest.TryGetValue(definition.TypeId, out var previous) || previous.Version < definition.Version)
                latest[definition.TypeId] = definition;
        }
    }

    public IEnumerable<NodeDefinition> Definitions => entries.Values;
    public IEnumerable<NodeDefinition> Latest => latest.Values;

    public NodeDefinition Get(string typeId, int version, string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(typeId) || !latest.ContainsKey(typeId))
            throw new GraphError("UnknownNodeType", nodeId ?? typeId);
        return entries.TryGetValue((typeId, version), out var definition) ? definition
            : throw new GraphError("UnknownNodeVersion", nodeId ?? typeId);
    }

    public NodeDefinition GetLatest(string typeId) => latest.TryGetValue(typeId, out var definition) ? definition
        : throw new GraphError("UnknownNodeType", typeId);

    public IEnumerable<PortDefinition> Ports(WorkflowNode node)
    {
        var definition = Get(node.TypeId, node.Version, node.Id);
        // ponytail: Phase 00 的动态端口试验节点沿用旧字段，普通插件只使用声明的端口。
        return node.TypeId == NodeTypes.Probe ? NodeTypes.Ports(node) : definition.Ports;
    }

    internal static void CheckDefinition(NodeDefinition definition)
    {
        static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace);
        if (!ValidId(definition.TypeId) || definition.Version < 1 || definition.Parameters is null || definition.Ports is null ||
            definition.Ports.Any(port => port is null || !ValidId(port.Id) ||
                port.Type is not ("String" or "Number" or "Boolean" or "Json" or "Model") || port.MaxConnections is <= 0 ||
                port.ResourceKind is { } resource && !ValidId(resource)) ||
            definition.Parameters.Any(parameter => parameter is null) ||
            definition.Ports.Select(port => port.Id).Distinct().Count() != definition.Ports.Count ||
            definition.Parameters.Select(parameter => parameter.Id).Distinct().Count() != definition.Parameters.Count ||
            (definition.LegacyTextParameter is { } legacy &&
                !definition.Parameters.Any(parameter => parameter.Id == legacy && parameter.Kind is "text" or "multiline")))
            throw new GraphError("InvalidNodeDefinition", definition.TypeId);
        foreach (var parameter in definition.Parameters)
        {
            if (!ValidId(parameter.Id) || parameter.Kind is not ("text" or "multiline" or "number" or "toggle" or "choice" or "json") ||
                parameter.Min > parameter.Max ||
                (parameter.Kind == "choice" && (parameter.Choices is not { Count: > 0 } ||
                    parameter.Choices.Any(choice => string.IsNullOrWhiteSpace(choice)) ||
                    parameter.Choices.Distinct(StringComparer.Ordinal).Count() != parameter.Choices.Count)) ||
                (parameter.Default.ValueKind != JsonValueKind.Undefined &&
                    NodeValidator.CheckValue(parameter, parameter.Default) is not null))
                throw new GraphError("InvalidNodeDefinition", definition.TypeId, parameterId: parameter.Id);
        }
    }
}
