using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentFlow.NodeHost;

internal static partial class FlowValues
{
    [GeneratedRegex(@"\A(?<path>\$|/\S*)\s+(?<op>==|!=|>=|<=|>|<)\s+(?<value>.+)\z", RegexOptions.Singleline)]
    private static partial Regex Comparison();

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]{0,39})\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]{0,39}\z")]
    public static partial Regex VariableName();

    public static Func<string, bool> Condition(string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
            throw new GraphError("InvalidCondition");
        var comparison = Comparison().Match(expression);
        if (!comparison.Success)
        {
            if (expression.StartsWith('$') || expression.StartsWith('/'))
                throw new GraphError("InvalidCondition");
            // 旧版画布保存的普通文本表达式使用精确匹配。
            return input => input == expression;
        }

        var path = comparison.Groups["path"].Value;
        path = path == "$" ? "" : path;
        var parts = Pointer(path);
        var op = comparison.Groups["op"].Value;
        var right = Json(comparison.Groups["value"].Value, "InvalidCondition");
        if (op is "==" or "!=")
            return input => JsonElement.DeepEquals(At(Value(input), parts), right) == (op == "==");
        var threshold = Number(right, "InvalidCondition");
        return input =>
        {
            var number = Number(At(Value(input), parts), "ConditionValueType");
            return op switch
            {
                ">" => number > threshold,
                "<" => number < threshold,
                ">=" => number >= threshold,
                _ => number <= threshold
            };
        };
    }

    public static string[] Pointer(string path)
    {
        if (path.Length == 0)
            return [];
        if (path[0] != '/' || path.Length > 512)
            throw new GraphError("InvalidJsonPointer");
        var parts = path[1..].Split('/');
        foreach (var part in parts)
        {
            for (var i = 0; i < part.Length; i++)
            {
                if (part[i] == '~' && (++i == part.Length || part[i] is not ('0' or '1')))
                    throw new GraphError("InvalidJsonPointer");
            }
        }
        return parts.Select(part => part.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal)).ToArray();
    }

    public static JsonElement At(JsonElement value, string[] pointer)
    {
        foreach (var part in pointer)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(part, out var member))
            {
                value = member;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                (part.Length == 1 || part[0] != '0') && index >= 0 && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            throw new GraphError("MissingJsonValue");
        }
        return value;
    }

    public static JsonElement Json(string text, string code = "InvalidJsonInput")
    {
        try { return JsonSerializer.Deserialize<JsonElement>(text); }
        catch (JsonException) { throw new GraphError(code); }
    }

    public static JsonElement Value(string text)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(text); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(text); }
    }

    private static decimal Number(JsonElement element, string code) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number) ? number : throw new GraphError(code);

    public static string Render(string template, string input, IReadOnlyDictionary<string, string> variables, int limit = 64000)
    {
        // 使用一遍替换。来自模型或用户的变量值不会重新解释为模板。
        var result = new StringBuilder();
        var previous = 0;
        foreach (Match match in Placeholder().Matches(template))
        {
            Append(template.AsSpan(previous, match.Index - previous));
            var name = match.Groups[1].Value;
            var text = name == "input" ? input :
                variables.TryGetValue(name, out var value) ? value : throw new GraphError("MissingVariable");
            Append(text);
            previous = match.Index + match.Length;
        }
        Append(template.AsSpan(previous));
        return result.ToString();

        void Append(ReadOnlySpan<char> text)
        {
            if (result.Length + text.Length > limit)
                throw new GraphError("OutputLimit");
            result.Append(text);
        }
    }

    public static Dictionary<string, string[]> Mapping(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object)
            throw new GraphError("InvalidJsonMapping");
        var result = new Dictionary<string, string[]>();
        foreach (var field in fields.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.String || field.Name.Length is 0 or > 100 ||
                !result.TryAdd(field.Name, Pointer(field.Value.GetString()!)) || result.Count > 32)
                throw new GraphError("InvalidJsonMapping");
        }
        if (result.Count == 0)
            throw new GraphError("InvalidJsonMapping");
        return result;
    }

    // ponytail: 先支持交付案例使用的结构关键字。拒绝其他关键字和外部引用，避免误判已完成校验。
    public sealed record Shape(string Kind, Dictionary<string, Shape>? Properties, HashSet<string>? Required,
        Shape? Items, bool Additional)
    {
        public static Shape Read(JsonElement schema, int depth = 0)
        {
            if (depth > 12 || schema.ValueKind != JsonValueKind.Object ||
                !schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new GraphError("InvalidOutputSchema");
            var kind = type.GetString();
            if (kind is not ("object" or "array" or "string" or "number" or "integer" or "boolean" or "null"))
                throw new GraphError("InvalidOutputSchema");

            Dictionary<string, Shape>? properties = null;
            HashSet<string>? required = null;
            Shape? items = null;
            var additional = true;
            var used = new HashSet<string>();
            foreach (var property in schema.EnumerateObject())
            {
                if (!used.Add(property.Name))
                    throw new GraphError("InvalidOutputSchema");
                var value = property.Value;
                switch (property.Name)
                {
                    case "type":
                        break;
                    case "properties" when kind == "object" && value.ValueKind == JsonValueKind.Object:
                        properties = [];
                        foreach (var field in value.EnumerateObject())
                        {
                            if (!properties.TryAdd(field.Name, Read(field.Value, depth + 1)))
                                throw new GraphError("InvalidOutputSchema");
                        }
                        break;
                    case "required" when kind == "object" && value.ValueKind == JsonValueKind.Array:
                        required = [];
                        foreach (var key in value.EnumerateArray())
                        {
                            if (key.ValueKind != JsonValueKind.String || !required.Add(key.GetString()!))
                                throw new GraphError("InvalidOutputSchema");
                        }
                        break;
                    case "items" when kind == "array":
                        items = Read(value, depth + 1);
                        break;
                    case "additionalProperties" when kind == "object" && value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        additional = value.GetBoolean();
                        break;
                    default:
                        throw new GraphError("InvalidOutputSchema");
                }
            }
            if (required is not null && required.Any(name => properties?.ContainsKey(name) != true))
                throw new GraphError("InvalidOutputSchema");
            return new(kind, properties, required, items, additional);
        }

        public bool Accepts(JsonElement value)
        {
            switch (Kind)
            {
                case "array":
                    return value.ValueKind == JsonValueKind.Array && (Items is null || value.EnumerateArray().All(Items.Accepts));
                case "object":
                    if (value.ValueKind != JsonValueKind.Object)
                        return false;
                    var keys = new HashSet<string>();
                    foreach (var field in value.EnumerateObject())
                    {
                        if (!keys.Add(field.Name) || (Properties?.TryGetValue(field.Name, out var shape) == true
                            ? !shape.Accepts(field.Value) : !Additional))
                            return false;
                    }
                    return Required is null || Required.IsSubsetOf(keys);
                case "string": return value.ValueKind == JsonValueKind.String;
                case "number": return value.ValueKind == JsonValueKind.Number;
                case "integer": return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number);
                case "boolean": return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                default: return value.ValueKind == JsonValueKind.Null;
            }
        }
    }
}
