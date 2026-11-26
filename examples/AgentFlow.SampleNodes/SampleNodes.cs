using System.Text.Json;

namespace AgentFlow.SampleNodes;

public sealed class SamplePlugin : INodePlugin
{
    public void Register(NodeRegistry registry)
    {
        registry.Register(new("sample.concat", 1, new("Concatenate text", "文本拼接", "文字串接"),
        [
            new("left", "String", Required: true, Label: new("Left text", "左文本", "左文字")),
            new("right", "String", Required: true, Label: new("Right text", "右文本", "右文字")),
            new("text", "String", Output: true, Required: true, Label: new("Result", "结果", "結果"))
        ],
        [
            new("separator", "text", new("Separator", "分隔符", "分隔符號"), Default: Value(" "),
                Help: new("Inserted between the two inputs.", "插在两段输入之间。", "插在兩段輸入之間。")),
            new("repeat", "number", new("Repeats", "重复次数", "重複次數"), Default: Value(1), Min: 1, Max: 4, Integer: true),
            new("casing", "choice", new("Letter case", "字母大小写", "字母大小寫"),
                Default: Value("keep"), Choices: ["keep", "upper", "lower"], ChoiceLabels: new Dictionary<string, LocalizedText>
                {
                    ["keep"] = new("Keep", "保留", "保留"),
                    ["upper"] = new("Uppercase", "转大写", "轉大寫"),
                    ["lower"] = new("Lowercase", "转小写", "轉小寫")
                })
        ], new("Joins two text inputs and repeats the result.", "拼接两段文本并重复结果。", "串接兩段文字並重複結果。"))
        {
            Documentation = new(
                new("Connect Left text and Right text. Enter a separator and choose the repeat count and letter case.",
                    "连接左、右文本，填写分隔符，再设置重复次数和字母大小写。",
                    "連接左、右文字，填寫分隔符號，再設定重複次數和字母大小寫。"),
                new("AgentFlow Node SDK adapter. A MAF Workflow Executor invokes the SDK factory after both inputs arrive.",
                    "AgentFlow Node SDK 适配器。两个输入到齐后，由 MAF Workflow Executor 调用 SDK 工厂。",
                    "AgentFlow Node SDK 配接器。兩個輸入到齊後，由 MAF Workflow Executor 呼叫 SDK 工廠。"))
        },
            () => new ConcatNode());

        registry.Register(new("sample.json-pick", 1, new("Pick JSON property", "提取 JSON 字段", "擷取 JSON 欄位"),
        [
            new("json", "Json", Required: true, Label: new("JSON input", "JSON 输入", "JSON 輸入")),
            new("out", "Json", Output: true, Required: true, Label: new("JSON value", "JSON 值", "JSON 值"))
        ],
        [
            new("property", "text", new("Property name", "字段名", "欄位名稱"), Required: true, Default: Value("value")),
            new("caseSensitive", "toggle", new("Match case", "区分大小写", "區分大小寫"), Default: Value(true)),
            new("fallback", "json", new("Fallback JSON", "备用 JSON", "備用 JSON"), Default: Value<object?>(null))
        ], new("Returns a property value, or the configured fallback.", "提取字段值，找不到时使用备用值。", "擷取欄位值，找不到時使用備用值。"))
        {
            Documentation = new(
                new("Connect a JSON object, enter the property name and choose whether case matters. Configure a fallback for a missing property.",
                    "连接 JSON 对象，填写字段名，并选择是否区分大小写。可以为缺失字段设置备用值。",
                    "連接 JSON 物件，填寫欄位名稱，並選擇是否區分大小寫。可以為缺少的欄位設定備用值。"),
                new("AgentFlow Node SDK data adapter. A MAF Workflow Executor invokes its factory and routes the typed JSON output to connected nodes.",
                    "AgentFlow Node SDK 数据适配器。MAF Workflow Executor 调用工厂，将 JSON 输出按连线传给下游节点。",
                    "AgentFlow Node SDK 資料配接器。MAF Workflow Executor 呼叫工廠，將 JSON 輸出依連線傳給下游節點。"))
        },
            () => new JsonPickNode());

        // 示例插件完成 String 与 Json 端口之间的显式转换，保持 SDK 的端口匹配严格。
        var mapping = new LocalizedText("Node SDK adapter executed by a MAF Workflow Executor.",
            "由 MAF Workflow Executor 调用的 Node SDK 适配器。", "由 MAF Workflow Executor 呼叫的 Node SDK 配接器。");
        registry.Register(new("sample.parse-json", 1, new("Parse JSON text", "解析 JSON 文本", "解析 JSON 文字"),
            [new("in", "String", Required: true), new("out", "Json", Output: true, Required: true)], [], ShowInLibrary: false)
        {
            Documentation = new(new("Feed JSON text into the String input to produce a typed JSON value.",
                "把 JSON 文本连入 String 端口，输出类型为 Json 的值。", "把 JSON 文字接入 String 連接埠，輸出型別為 Json 的值。"), mapping)
        }, () => new JsonTextAdapter(parse: true));
        registry.Register(new("sample.write-json", 1, new("Write JSON text", "生成 JSON 文本", "產生 JSON 文字"),
            [new("in", "Json", Required: true), new("out", "String", Output: true, Required: true)], [], ShowInLibrary: false)
        {
            Documentation = new(new("Connect a typed JSON value and send its serialized text to Output.",
                "接入类型为 Json 的值，将序列化后的文本连接到输出。", "接入型別為 Json 的值，將序列化後的文字連接至輸出。"), mapping)
        }, () => new JsonTextAdapter(parse: false));
    }

    private static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class JsonTextAdapter(bool parse) : INodeExecutor
    {
        public ValueTask<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = context.Inputs["in"];
            try
            {
                var output = parse ? JsonSerializer.Deserialize<JsonElement>(input.GetString()!) : Value(input.GetRawText());
                return ValueTask.FromResult(new NodeResult(new Dictionary<string, JsonElement> { ["out"] = output }));
            }
            catch (JsonException)
            {
                throw new GraphError("InvalidJsonInput", context.NodeId);
            }
        }
    }

    private sealed class ConcatNode : INodeExecutor
    {
        public ValueTask<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = context.Inputs["left"].GetString() + context.Parameters["separator"].GetString() +
                context.Inputs["right"].GetString();
            text = context.Parameters["casing"].GetString() switch
            {
                "upper" => text.ToUpperInvariant(),
                "lower" => text.ToLowerInvariant(),
                _ => text
            };
            return ValueTask.FromResult(new NodeResult(new Dictionary<string, JsonElement>
            {
                ["text"] = Value(string.Concat(Enumerable.Repeat(text, (int)context.Parameters["repeat"].GetDecimal())))
            }));
        }
    }

    private sealed class JsonPickNode : INodeExecutor
    {
        public ValueTask<NodeResult> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = context.Inputs["json"];
            var name = context.Parameters["property"].GetString();
            var comparison = context.Parameters["caseSensitive"].GetBoolean()
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var value = context.Parameters["fallback"];
            if (input.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in input.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, comparison))
                        continue;
                    value = property.Value;
                    break;
                }
            }
            return ValueTask.FromResult(new NodeResult(new Dictionary<string, JsonElement> { ["out"] = value }));
        }
    }
}
