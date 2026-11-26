using System.Text.Json;

namespace AgentFlow;

public sealed record NodeIssue(string Code, string Field);
public sealed record ParameterValidation(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<NodeIssue> Issues);

public interface INodeValidator
{
    ParameterValidation Validate(NodeDefinition definition, NodeConfig config);
}

public sealed class NodeValidator : INodeValidator
{
    public ParameterValidation Validate(NodeDefinition definition, NodeConfig config) => Resolve(definition, config);

    public static ParameterValidation Resolve(NodeDefinition definition, NodeConfig config)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var issues = new List<NodeIssue>();
        if (config.Parameters is { } given)
        {
            foreach (var id in given.Keys.Where(id => !definition.Parameters.Any(parameter => parameter.Id == id)))
                issues.Add(new("UnknownParameter", id));
        }
        foreach (var parameter in definition.Parameters)
        {
            var value = ReadValue(definition, parameter, config);
            if (value is null)
            {
                if (parameter.Required)
                    issues.Add(new("RequiredParameter", parameter.Id));
                continue;
            }
            var error = CheckValue(parameter, value.Value);
            if (error is not null)
                issues.Add(new(error, parameter.Id));
            else
                values.Add(parameter.Id, value.Value);
        }
        return new(values, issues);
    }

    public static JsonElement? ReadValue(NodeDefinition definition, ParameterDefinition parameter, NodeConfig config)
    {
        if (definition.LegacyTextParameter == parameter.Id && config.Text is { } legacy)
            return JsonSerializer.SerializeToElement(legacy);
        if (config.Parameters?.TryGetValue(parameter.Id, out var value) == true)
            return value;
        return parameter.Default.ValueKind == JsonValueKind.Undefined ? null : parameter.Default;
    }

    internal static string? CheckValue(ParameterDefinition parameter, JsonElement value)
    {
        switch (parameter.Kind)
        {
            case "text":
            case "multiline":
                if (value.ValueKind != JsonValueKind.String)
                    return "ParameterType";
                return parameter.Required && string.IsNullOrWhiteSpace(value.GetString()) ? "RequiredParameter" : null;
            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                    return "ParameterType";
                if ((parameter.Min is { } min && number < min) || (parameter.Max is { } max && number > max) ||
                    (parameter.Integer && number != decimal.Truncate(number)))
                    return "ParameterRange";
                return null;
            case "toggle":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "ParameterType";
            case "choice":
                return value.ValueKind == JsonValueKind.String && parameter.Choices!.Contains(value.GetString())
                    ? null : "ParameterChoice";
            case "json":
                return value.ValueKind == JsonValueKind.Undefined ? "ParameterType" : null;
            default:
                return "ParameterType";
        }
    }

    internal static bool PortAccepts(string type, JsonElement value) => type switch
    {
        "String" or "Model" => value.ValueKind == JsonValueKind.String,
        "Number" => value.ValueKind == JsonValueKind.Number,
        "Boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "Json" => value.ValueKind != JsonValueKind.Undefined,
        _ => false
    };
}
