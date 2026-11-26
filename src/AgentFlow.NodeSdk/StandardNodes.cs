using System.Text.Json;

namespace AgentFlow;

public static class StandardNodes
{
    public const int HarnessMaxIterations = 5;
    public const int MaxAgentToolBindings = 4;
    public const int MaxToolCallsPerTurn = 4;

    public static NodeCatalog Catalog { get; } = new(
    [
        BuiltIn(NodeTypes.Input, new("Text input", "文本输入", "文字輸入"), new("Text", "文本", "文字")),
        BuiltIn(NodeTypes.Model, new("Chat model", "聊天模型", "聊天模型"), new("Model profile", "模型配置", "模型設定")),
        Agent(NodeTypes.Agent, new("ChatClientAgent", "聊天客户端智能体", "聊天用戶端代理程式")),
        BuiltIn(NodeTypes.HarnessAgent, new("HarnessAgent", "Harness 智能体", "Harness 代理程式"),
            new("Task instructions", "任务指令", "任務指令")),
        BuiltIn(NodeTypes.HarnessInstructions, new("Harness instructions", "Harness 执行指引", "Harness 執行指引"),
            new("Instructions", "指引", "指引")),
        BuiltIn(NodeTypes.HarnessMcpTools, new("Harness MCP tools", "Harness MCP 工具", "Harness MCP 工具"),
            parameters: [ToolBindings()]),
        BuiltIn(NodeTypes.HarnessFunctionLoop, new("Function invocation loop", "函数调用循环", "函式呼叫迴圈"),
            parameters: [Count("maximumIterations", new("Loop limit", "循环上限", "迴圈上限"),
                HarnessMaxIterations, 1, HarnessMaxIterations),
                Count("maxToolCalls", new("MCP calls per turn", "每轮 MCP 调用上限", "每輪 MCP 呼叫上限"),
                    MaxToolCallsPerTurn, 1, MaxToolCallsPerTurn)]),
        BuiltIn(NodeTypes.HarnessCompaction, new("Context compaction", "上下文压缩", "上下文壓縮"),
            parameters: [Count("contextWindowTokens", new("Context window (tokens)", "上下文窗口（Token）", "上下文視窗（Token）"), 0, 0, int.MaxValue)
                with { Help = new("Enter the model's context window to enable MAF's preview compaction. Zero disables it.",
                    "填写模型的上下文窗口来启用 MAF 预览压缩。设为 0 时关闭。", "填寫模型的上下文視窗來啟用 MAF 預覽壓縮。設為 0 時關閉。") }]),
        BuiltIn(NodeTypes.Prompt, new("Prompt", "提示词", "提示詞"), new("Template", "模板", "範本")),
        BuiltIn(NodeTypes.Condition, new("Condition", "条件", "條件"), new("Expression", "表达式", "運算式")),
        BuiltIn(NodeTypes.Parallel, new("Parallel", "并行", "平行"), new("Branch labels", "分支名称", "分支名稱")),
        BuiltIn(NodeTypes.Merge, new("Merge", "合并", "合併"), new("Output format", "输出格式", "輸出格式"),
            parameters: [new("mode", "choice", new("Wait for", "等待条件", "等待條件"),
                Default: J("all"), Choices: ["all", "any"],
                ChoiceLabels: new Dictionary<string, LocalizedText>
                {
                    ["all"] = new("All connected inputs", "全部已连接输入", "全部已連接輸入"),
                    ["any"] = new("One active input", "一个有效输入", "一個有效輸入")
                })]),
        BuiltIn(NodeTypes.Debug, new("Debug", "调试", "偵錯"), new("Label", "标签", "標籤")),
        BuiltIn(NodeTypes.Output, new("Output", "输出", "輸出")),
        BuiltIn(NodeTypes.RunTerminal, new("Run Terminal", "运行终端", "執行終端")),
        BuiltIn(NodeTypes.Switch, new("Switch", "多路选择", "多路選擇"), parameters:
        [
            new("path", "text", new("JSON Pointer", "JSON 指针", "JSON 指標"), Default: J("")),
            new("a", "json", new("Case A", "匹配值 A", "比對值 A"), Default: J("a")),
            new("b", "json", new("Case B", "匹配值 B", "比對值 B"), Default: J("b"))
        ]),
        BuiltIn(NodeTypes.Loop, new("Loop", "循环", "迴圈"), parameters:
        [
            Count("iterations", new("Max iterations", "最大轮数", "最大輪數"), 3, 1, 20),
            new("until", "text", new("Stop when", "停止条件", "停止條件"),
                Help: new("Example: /score >= 80", "示例：/score >= 80", "範例：/score >= 80"))
        ]),
        BuiltIn(NodeTypes.ForEach, new("For each", "逐项处理", "逐項處理"), parameters:
            [Count("items", new("Max items", "最大项数", "最大項數"), 32, 1, 100)]),
        BuiltIn(NodeTypes.Retry, new("Retry model", "模型重试", "模型重試"), new("Instructions", "指令", "指令"), parameters:
        [
            Count("attempts", new("Max attempts", "最大尝试次数", "最大嘗試次數"), 3, 1, 5),
            Count("milliseconds", new("Retry delay (ms)", "重试间隔（毫秒）", "重試間隔（毫秒）"), 200, 0, 10000)
        ]),
        BuiltIn(NodeTypes.Delay, new("Delay", "延时", "延時"), parameters:
            [Count("milliseconds", new("Delay (ms)", "延时（毫秒）", "延時（毫秒）"), 250, 0, 30000)]),
        BuiltIn(NodeTypes.StructuredOutput, new("Structured output", "结构化输出", "結構化輸出"), parameters:
            [new("schema", "json", new("JSON Schema", "JSON Schema", "JSON Schema"), Required: true)]),
        BuiltIn(NodeTypes.Variable, new("Set variable", "设置变量", "設定變數"), parameters:
            [new("name", "text", new("Variable name", "变量名", "變數名稱"), Required: true)]),
        BuiltIn(NodeTypes.JsonMapper, new("JSON mapper", "JSON 映射", "JSON 對應"), parameters:
            [new("fields", "json", new("Fields and JSON pointers", "字段与 JSON 指针", "欄位與 JSON 指標"), Required: true)]),
        BuiltIn(NodeTypes.HttpRead, new("HTTP read", "HTTP 读取", "HTTP 讀取"), parameters:
            [new("connection", "text", new("HTTP connection", "HTTP 连接标识", "HTTP 連線識別碼"), Required: true), Deadline()]),
        BuiltIn(NodeTypes.FileRead, new("Read file", "读取文件", "讀取檔案"), parameters:
            [new("path", "text", new("Relative path", "相对路径", "相對路徑"), Required: true), Deadline()]),
        BuiltIn(NodeTypes.SqliteQuery, new("SQLite query", "SQLite 查询", "SQLite 查詢"), parameters:
        [
            new("source", "text", new("Database source", "数据源标识", "資料來源識別碼"), Required: true),
            new("columns", "json", new("Columns", "结果列", "結果欄"), Required: true),
            new("equals", "json", new("Equality filters", "等值筛选", "等值篩選"), Default: J(new Dictionary<string, object>())),
            Count("rows", new("Row limit", "行数上限", "列數上限"), 20, 1, 100), Deadline()
        ]),
        BuiltIn(NodeTypes.McpTool, new("MCP tool", "MCP 工具", "MCP 工具"), parameters:
        [
            new("server", "text", new("MCP server", "MCP 服务标识", "MCP 伺服器識別碼"), Required: true),
            new("tool", "text", new("Tool name", "工具名称", "工具名稱"), Required: true),
            new("arguments", "json", new("Arguments", "工具参数", "工具參數"), Default: J(new Dictionary<string, object>())),
            new("useInput", "toggle", new("Use input as arguments", "使用输入作为参数", "使用輸入作為參數"), Default: J(false)),
            Deadline()
        ]),
        BuiltIn(NodeTypes.JsonTransform, new("JSON transform", "JSON 转换", "JSON 轉換"), parameters:
        [
            new("path", "text", new("JSON Pointer", "JSON 指针", "JSON 指標"), Default: J("")),
            new("format", "choice", new("Output format", "输出格式", "輸出格式"), Default: J("json"), Choices: ["json", "text"],
                ChoiceLabels: new Dictionary<string, LocalizedText> { ["json"] = new("JSON", "JSON", "JSON"), ["text"] = new("Text", "文本", "文字") })
        ]),
        BuiltIn(NodeTypes.Subworkflow, new("Sub-workflow", "子工作流", "子工作流程"), parameters:
        [
            new("workflow", "text", new("Workflow ID", "工作流标识", "工作流程識別碼"), Required: true),
            Count("revision", new("Locked revision", "锁定版本", "鎖定版本"), 1, 1, int.MaxValue),
            new("inputName", "text", new("Input name", "输入名称", "輸入名稱"), Default: J("Input")),
            new("outputName", "text", new("Output name", "输出名称", "輸出名稱"), Default: J("Output"))
        ]),
        BuiltIn(NodeTypes.Approval, new("Approval", "人工审批", "人工審批"), parameters: HumanFields()),
        BuiltIn(NodeTypes.UserInput, new("User input", "人工输入", "人工輸入"), parameters: HumanFields()),
        BuiltIn(NodeTypes.SimulatedWrite, new("Simulated write", "模拟写入", "模擬寫入"), show: false),
        BuiltIn(NodeTypes.Normalize, new("Trim whitespace", "清除首尾空白", "清除頭尾空白"), show: false),
        BuiltIn(NodeTypes.Probe, new("Port probe", "端口试验", "連接埠測試"), show: false)
    ]);

    public static void Register(NodeRegistry registry)
    {
        foreach (var definition in Catalog.Definitions)
            registry.Register(definition);
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static NodeDefinition Agent(string id, LocalizedText name) =>
        BuiltIn(id, name, new("Instructions", "指令", "指令"), parameters: [ToolBindings()]);

    private static ParameterDefinition ToolBindings() =>
        new("tools", "json", new("MCP tool bindings", "MCP 工具绑定", "MCP 工具繫結"), Default: J(Array.Empty<string>()),
            Help: new("Approved server/tool references", "已授权的服务器/工具引用", "已授權的伺服器/工具參照"));

    private static ParameterDefinition Count(string id, LocalizedText label, int value, int min, int max) =>
        new(id, "number", label, Default: J(value), Min: min, Max: max, Integer: true);

    private static ParameterDefinition Deadline() =>
        Count("seconds", new("Timeout (s)", "超时（秒）", "逾時（秒）"), 10, 1, 30);

    private static ParameterDefinition[] HumanFields() =>
    [
        new("assignee", "text", new("Assignee", "处理人", "處理人"), Required: true),
        new("prompt", "multiline", new("Instructions", "处理说明", "處理說明"), Required: true),
        Count("seconds", new("Wait timeout (s)", "等待超时（秒）", "等待逾時（秒）"), 300, 1, 86400)
    ];

    private static NodeDefinition BuiltIn(string id, LocalizedText name, LocalizedText? parameter = null, bool show = true,
        ParameterDefinition[]? parameters = null)
    {
        var node = new WorkflowNode("", id, 1, new NodeConfig(Ports: id == NodeTypes.Probe ? 1 : 0));
        return new(id, 1, name, NodeTypes.Ports(node).Select(port => port with
        {
            Label = port.Label ?? port.Id switch
            {
                "in" => new("Input", "输入", "輸入"),
                "text" => new("Text", "文本", "文字"),
                "number" => new("Number", "数字", "數字"),
                "value" => new("Value", "值", "值"),
                "model" => new("Model", "模型", "模型"),
                "yes" => new("True", "为真", "為真"),
                "no" => new("False", "为假", "為假"),
                "a" => new("Branch A", "分支 A", "分支 A"),
                "b" => new("Branch B", "分支 B", "分支 B"),
                "c" => new("Branch C", "分支 C", "分支 C"),
                "other" => new("Other", "其他", "其他"),
                "feedback" => new("Feedback", "反馈", "回饋"),
                "body" => new("Body", "循环体", "迴圈本體"),
                "done" => new("Done", "完成", "完成"),
                _ => null
            }
        }).ToArray(), parameter is null ? parameters ?? [] : [new("text", "multiline", parameter), .. parameters ?? []],
            ShowInLibrary: show, LegacyTextParameter: parameter is null ? null : "text")
        {
            Documentation = StandardNodeDocumentation.Get(id)
        };
    }
}
