namespace AgentFlow;

internal static class StandardNodeDocumentation
{
    private const string Workflow = "https://learn.microsoft.com/agent-framework/concepts/workflows/";
    private const string Edges = "https://learn.microsoft.com/agent-framework/concepts/workflows/edges";
    private const string Agent = "https://learn.microsoft.com/agent-framework/agents/agent-pipeline";
    private const string Executor = "https://learn.microsoft.com/agent-framework/concepts/workflows/advanced/agent-executor";
    private const string Human = "https://learn.microsoft.com/agent-framework/workflows/human-in-the-loop";
    private const string Subworkflow = "https://learn.microsoft.com/agent-framework/concepts/workflows/advanced/sub-workflows";
    private const string Harness = "https://learn.microsoft.com/en-us/agent-framework/concepts/harness";

    public static NodeDocumentation Get(string typeId) => typeId switch
    {
        NodeTypes.Input => new(
            new("Enter the starting text and connect its Text output to the first step. Example: send a question to an Agent.",
                "填写起始文本，再将「文本」输出连到第一步。例如，把提问送给智能体。",
                "填寫起始文字，再將「文字」輸出連到第一步。例如，把問題送給代理程式。"),
            new("AgentFlow input adapter at the WorkflowBuilder entry. It turns the configured text into the first workflow message.",
                "AgentFlow 的输入适配器，绑定到 WorkflowBuilder 入口，把配置的文本转成首条工作流消息。",
                "AgentFlow 的輸入配接器，繫結至 WorkflowBuilder 入口，將設定文字轉成首筆工作流程訊息。"), Workflow),

        NodeTypes.Output => new(
            new("Connect the previous step to Value. Run the workflow to see this node’s result and open its details. Add another Output for a separate result.",
                "把上游结果接到「值」。运行后在本节点查看结果和详情。独立结果可分别接到不同的输出节点。",
                "將上游結果接到「值」。執行後可在本節點查看結果與詳細資料。各項獨立結果可接到不同輸出節點。"),
            new("The host designates this executor with WorkflowBuilder.WithOutputFrom. Each result is recorded under its output node ID.",
                "宿主使用 WorkflowBuilder.WithOutputFrom 指定该执行器为输出，按输出节点 ID 记录结果。",
                "宿主以 WorkflowBuilder.WithOutputFrom 指定此執行器為輸出，並依輸出節點 ID 記錄結果。"), Workflow),

        NodeTypes.RunTerminal => new(
            new("Run or step through the workflow here. Select an event to inspect its node or a recorded run to replay it.",
                "在终端运行或单步执行工作流。选择事件检查节点，或打开历史运行进行回放。",
                "在終端執行或逐步執行工作流程。選取事件檢查節點，或開啟歷史執行進行回放。"),
            new("Editor control for the host run and MAF workflow events. The terminal uses the workflow's RunId.",
                "编辑器通过终端控制宿主运行并显示 MAF 工作流事件。终端使用当前工作流的 RunId。",
                "編輯器透過終端控制宿主執行並顯示 MAF 工作流程事件。終端使用目前工作流程的 RunId。"), Workflow),

        NodeTypes.Model => new(
            new("Create a profile in Models, select it here and connect the Model port to an Agent or Retry model.",
                "在「模型管理」创建配置，然后在这里选择，并将「模型」端口接到智能体或模型重试节点。",
                "在「模型管理」建立設定，再於此選取，並將「模型」連接埠接至代理程式或模型重試節點。"),
            new("AgentFlow resource binding. The profile supplies the IChatClient used by ChatClientAgent.",
                "AgentFlow 的资源绑定。模型配置提供 ChatClientAgent 调用的 IChatClient。",
                "AgentFlow 的資源繫結。模型設定提供 ChatClientAgent 使用的 IChatClient。"), Agent),

        NodeTypes.Agent => new(
            new("Connect text to Input and a chat model to Model. Add instructions and, when needed, approved MCP tool bindings. Connect Text to the next step.",
                "连接文本输入和聊天模型。填写指令，需要时绑定已授权的 MCP 工具。将「文本」输出接到下一步。",
                "接上文字輸入和聊天模型。填寫指令，視需要繫結已授權的 MCP 工具。將「文字」輸出接到下一步。"),
            new("MAF Agent Executor schedules ChatClientAgent in regular runs. AgentFlow adapts messages and records model activity. Feedback loops and node stepping use the compatibility executor.",
                "常规运行由 MAF Agent Executor 调度 ChatClientAgent。AgentFlow 负责消息适配和模型调用记录。含反馈循环或节点步进的运行使用兼容执行器。",
                "一般執行由 MAF Agent Executor 排程 ChatClientAgent。AgentFlow 負責訊息配接及模型呼叫記錄。含回饋迴圈或節點逐步執行的流程使用相容執行器。"), Executor),

        NodeTypes.HarnessAgent => new(
            new("Connect the incoming message and Chat Model. Add task instructions here and wire any capability nodes to the matching resource ports. Connect Text to the next step.",
                "接入上游消息和聊天模型，在本节点填写任务指令。按需将能力节点连到对应资源口，再把「文本」输出接到下一步。",
                "接入上游訊息與聊天模型，在本節點填寫任務指令。視需要將能力節點連至對應資源埠，再把「文字」輸出接到下一步。"),
            new("MAF Workflow schedules HarnessAgent as an Agent Executor. AgentFlow resolves the resources before execution. Harness handles tool turns and AgentSession.",
                "MAF Workflow 将 HarnessAgent 作为 Agent Executor 调度。AgentFlow 在执行前解析资源，Harness 管理工具轮次和 AgentSession。",
                "MAF Workflow 將 HarnessAgent 作為 Agent Executor 排程。AgentFlow 在執行前解析資源，Harness 管理工具輪次和 AgentSession。"), Harness),

        NodeTypes.HarnessInstructions => new(
            new("Write guidance that can be shared by several Harness Agents. Connect this node to their Harness instructions ports.",
                "填写可由多个 HarnessAgent 共用的执行指引，连到各智能体的「执行指引」口。",
                "填寫可由多個 HarnessAgent 共用的執行指引，連至各代理程式的「執行指引」埠。"),
            new("The host supplies this resource to HarnessAgentOptions.HarnessInstructions before the Agent Executor runs.",
                "宿主在 Agent Executor 运行前将资源映射到 HarnessAgentOptions.HarnessInstructions。",
                "宿主在 Agent Executor 執行前將資源對應至 HarnessAgentOptions.HarnessInstructions。"), Harness),

        NodeTypes.HarnessMcpTools => new(
            new("Select tools already approved in Resources and connect to HarnessAgent's MCP tools port.",
                "选择在资源管理中已登记并授权的 MCP 工具，再连到 HarnessAgent 的「MCP 工具」口。",
                "選取在資源管理中已登記並授權的 MCP 工具，再連到 HarnessAgent 的「MCP 工具」埠。"),
            new("AgentFlow supplies authorized read-only AIFunctions through ChatOptions.Tools. MAF Harness manages invocation and the host audits calls.",
                "AgentFlow 通过 ChatOptions.Tools 提供已授权的只读 AIFunction。MAF Harness 管理调用，宿主记录审计。",
                "AgentFlow 透過 ChatOptions.Tools 提供已授權的唯讀 AIFunction。MAF Harness 管理呼叫，宿主記錄稽核。"), Harness),

        NodeTypes.HarnessFunctionLoop => new(
            new("Set the function loop and MCP call limits for one turn and connect to a Harness Agent.",
                "设置单轮函数循环和 MCP 调用上限，并连到 HarnessAgent 的「函数调用循环」口。",
                "設定單輪函式迴圈及 MCP 呼叫上限，並連到 HarnessAgent 的「函式呼叫迴圈」埠。"),
            new("The loop limit maps to HarnessAgentOptions.MaximumIterationsPerRequest. The host enforces the MCP call budget.",
                "循环上限映射到 HarnessAgentOptions.MaximumIterationsPerRequest，MCP 调用预算由宿主执行。",
                "迴圈上限對應至 HarnessAgentOptions.MaximumIterationsPerRequest，MCP 呼叫預算由宿主執行。"), Harness),

        NodeTypes.HarnessCompaction => new(
            new("Set the model context window and connect to HarnessAgent. Enter zero to turn compaction off.",
                "填写模型上下文窗口，连到 HarnessAgent 的「上下文压缩」口。设为 0 时关闭压缩。",
                "填寫模型上下文視窗，連到 HarnessAgent 的「上下文壓縮」埠。設為 0 時關閉壓縮。"),
            new("Uses MAF's preview compaction settings with the linked model's output token budget.",
                "使用 MAF 的预览压缩配置，并读取所连模型的输出 Token 预算。",
                "使用 MAF 的預覽壓縮設定，並讀取所連模型的輸出 Token 預算。"), Harness),

        NodeTypes.Prompt => new(
            new("Connect text to Input and enter a template, for example Summarize: {{input}}. Use {{name}} to read a variable set upstream.",
                "把消息接到输入并填写模板，例如「摘要：{{input}}」。用 {{name}} 引用上游已设置的变量。",
                "將訊息接到輸入並填寫範本，例如「摘要：{{input}}」。以 {{name}} 參照上游設定的變數。"),
            new("AgentFlow text adapter executed by MAF. Template substitution is implemented by AgentFlow.",
                "由 MAF 调度的 AgentFlow 文本适配器。模板插值由 AgentFlow 实现。",
                "由 MAF 排程的 AgentFlow 文字配接器。範本插值由 AgentFlow 實作。")),

        NodeTypes.Condition => new(
            new("Send text or JSON to Input. Enter a comparison such as /score >= 80. Wire the True and False outputs to their respective branches.",
                "把文本或 JSON 接到输入，填写表达式，例如 /score >= 80。将「为真」和「为假」分别接到对应分支。",
                "將文字或 JSON 接到輸入，填寫運算式，例如 /score >= 80。將「為真」和「為假」分別接到對應分支。"),
            new("AgentFlow evaluates the expression and routes the result through MAF conditional edges, built with AddEdge<T>.",
                "AgentFlow 求值，结果通过 MAF 条件边路由，装配使用 AddEdge<T>。",
                "AgentFlow 求值，再透過 MAF 條件邊路由；裝配使用 AddEdge<T>。"), Edges),

        NodeTypes.Parallel => new(
            new("Connect the upstream message and use branches A, B and C for independent work. Connect their results to Merge when you need to wait for all of them.",
                "接收上游消息，用 A、B、C 分别连接可独立执行的步骤。需要等待各路结果时，把它们接到合并节点。",
                "接收上游訊息，使用 A、B、C 分別連接可獨立執行的步驟。需要等待各路結果時，將它們接至合併節點。"),
            new("Maps to MAF fan-out. AgentFlow keeps the branch ports and applies the workflow’s execution limits.",
                "对应 MAF 扇出。AgentFlow 保留分支端口并应用工作流执行上限。",
                "對應 MAF 扇出。AgentFlow 保留分支連接埠並套用工作流程執行上限。"), Edges),

        NodeTypes.Merge => new(
            new("Use All for independent branches and One active input for exclusive branches. Select combine for lines of text or json for an array.",
                "独立分支选择「全部已连接输入」，互斥分支选择「一个有效输入」。格式 combine 按行拼接，json 生成数组。",
                "獨立分支選擇「全部已連接輸入」，互斥分支選擇「一個有效輸入」。格式 combine 按行串接，json 產生陣列。"),
            new("Eligible fixed-source graphs use MAF AddFanInBarrierEdge. AgentFlow handles conditional joins and result aggregation.",
                "固定来源且满足条件时使用 MAF AddFanInBarrierEdge。条件汇合和数据聚合由 AgentFlow 处理。",
                "固定來源且符合條件時使用 MAF AddFanInBarrierEdge。條件匯合和資料彙整由 AgentFlow 處理。"), Edges),

        NodeTypes.Debug => new(
            new("Insert it between two steps. Run and inspect its recorded input and output in Diagnostics. The message continues downstream.",
                "插入两步之间，运行后在「诊断」查看本节点记录的输入与输出。消息会继续向下游传递。",
                "插入兩步之間，執行後在「診斷」查看本節點記錄的輸入與輸出。訊息會繼續傳至下游。"),
            new("AgentFlow pass-through executor. The host records trace events around MAF execution.",
                "AgentFlow 的透传执行器。宿主在 MAF 执行过程中记录跟踪事件。",
                "AgentFlow 的直通執行器。宿主在 MAF 執行過程中記錄追蹤事件。")),

        NodeTypes.Switch => new(
            new("Set a JSON Pointer such as /state, then define distinct values for A and B. Wire Other for values that match neither. An empty pointer selects the entire input.",
                "填写 JSON 指针，例如 /state，再设置不同的 A、B 匹配值。剩余情况走「其他」。指针留空表示整个输入。",
                "填寫 JSON 指標，例如 /state，再設定不同的 A、B 比對值。其餘情況走「其他」。空白指標表示整筆輸入。"),
            new("Represents MAF Switch-Case. The current host uses conditional edges; AgentFlow evaluates the JSON Pointer and cases.",
                "对应 MAF 的 Switch-Case。当前宿主以条件边实现，JSON 指针与匹配值由 AgentFlow 求值。",
                "對應 MAF 的 Switch-Case。目前宿主以條件邊實作，JSON 指標與比對值由 AgentFlow 求值。"), Edges),

        NodeTypes.Loop => new(
            new("Connect an initial value to Input. Send Body through the steps and connect the last step back to Feedback. Set a round limit and connect Done.",
                "将初值接到输入，从「循环体」进入处理链，末端接回「反馈」。设置轮数上限，并连接「完成」输出。",
                "將初始值接到輸入，從「迴圈本體」進入處理鏈，末端接回「回饋」。設定輪數上限，並連接「完成」輸出。"),
            new("Uses MAF feedback and conditional edges. AgentFlow handles iteration limits and stop expressions.",
                "使用 MAF 反馈边和条件边。轮数控制及停止表达式由 AgentFlow 处理。",
                "使用 MAF 回饋邊和條件邊。輪數控制與停止運算式由 AgentFlow 處理。"), Edges),

        NodeTypes.ForEach => new(
            new("Send a JSON array to Input. Connect Body to a processing chain and its result back to Feedback. Done returns the results in array order.",
                "将 JSON 数组接到输入，从「循环体」连接处理链，结果接回「反馈」。「完成」按原数组顺序返回处理结果。",
                "將 JSON 陣列接到輸入，從「迴圈本體」連接處理鏈，結果接回「回饋」。「完成」依原陣列順序傳回處理結果。"),
            new("MAF routes feedback. The current AgentFlow adapter iterates and collects results as a JSON array of strings.",
                "MAF 负责反馈路由。当前 AgentFlow 适配器管理逐项迭代，并将结果收集为 JSON 字符串数组。",
                "MAF 負責回饋路由。目前 AgentFlow 配接器管理逐項迭代，並將結果收集為 JSON 字串陣列。"), Edges),

        NodeTypes.Retry => new(
            new("Connect text and a model, enter instructions, then choose the attempt count and delay. Transient failures can retry; the node emits the successful result.",
                "连接文本和模型，输入指令，再设置尝试次数和间隔。遇到可重试的失败会再次尝试，成功后输出该次结果。",
                "連接文字和模型，輸入指令，再設定嘗試次數與間隔。遇到可重試的失敗會再次嘗試，成功後輸出該次結果。"),
            new("AgentFlow execution policy around MAF ChatClientAgent calls. The workflow schedules the model step once.",
                "AgentFlow 在 MAF ChatClientAgent 调用外应用重试策略，工作流对该模型步骤只调度一次。",
                "AgentFlow 在 MAF ChatClientAgent 呼叫外套用重試策略，工作流程對該模型步驟只排程一次。"), Agent),

        NodeTypes.Delay => new(
            new("Set the wait in milliseconds and connect the node in a message chain. The same message continues after the wait.",
                "填写等待毫秒数并接入消息链。等待结束后继续传递原消息。",
                "填寫等待毫秒數並接入訊息鏈。等待完成後繼續傳遞原訊息。"),
            new("AgentFlow asynchronous waiting adapter scheduled by MAF. It observes the execution cancellation token.",
                "由 MAF 调度的 AgentFlow 异步等待适配器，响应本次运行的取消信号。",
                "由 MAF 排程的 AgentFlow 非同步等待配接器，會回應此次執行的取消訊號。")),

        NodeTypes.StructuredOutput => new(
            new("Connect JSON text and enter the supported JSON Schema, including fields such as type, properties and required. Invalid input fails the node.",
                "接入 JSON 文本并填写支持的 JSON Schema，可用 type、properties、required 等字段。不满足约束时节点失败。",
                "接入 JSON 文字並填寫支援的 JSON Schema，可用 type、properties、required 等欄位。條件不符時節點失敗。"),
            new("AgentFlow validation executor scheduled by MAF. It checks the received JSON after upstream execution.",
                "由 MAF 调度的 AgentFlow 验证执行器，在上游执行完成后校验收到的 JSON。",
                "由 MAF 排程的 AgentFlow 驗證執行器，在上游執行完成後驗證收到的 JSON。")),

        NodeTypes.Variable => new(
            new("Set a name and connect a value. A downstream Prompt or Agent can reference that value with {{name}} on the same path.",
                "设置变量名并连接输入值。同一路径下游的提示词或智能体可通过 {{name}} 引用。",
                "設定變數名稱並連接輸入值。同一路徑下游的提示詞或代理程式可使用 {{name}} 參照。"),
            new("The variable is carried by AgentFlow in the message sent through MAF edges. Its scope follows that message path.",
                "变量由 AgentFlow 随消息通过 MAF 边传递，作用域跟随消息路径。",
                "變數由 AgentFlow 隨訊息透過 MAF 邊傳遞，作用域跟隨訊息路徑。")),

        NodeTypes.JsonMapper => new(
            new("Connect a JSON document and map output names to input pointers. For example, use {\"score\":\"/review/score\"} to produce a score field.",
                "接入 JSON，把输出字段名映射到输入指针。例如配置 {\"score\":\"/review/score\"} 生成 score 字段。",
                "接入 JSON，將輸出欄位名稱對應至輸入指標。例如設定 {\"score\":\"/review/score\"} 以產生 score 欄位。"),
            new("AgentFlow data transformation executor scheduled by MAF. Field mapping and validation are implemented locally.",
                "由 MAF 调度的 AgentFlow 数据转换执行器。字段映射和校验在项目中实现。",
                "由 MAF 排程的 AgentFlow 資料轉換執行器。欄位對應與驗證在專案中實作。")),

        NodeTypes.HttpRead => new(
            new("Pick a registered HTTP connection and set the timeout. Connect an upstream message to trigger GET and pass the returned text or JSON downstream.",
                "选择宿主登记的 HTTP 连接并设置超时。用上游消息触发 GET，返回文本或 JSON 传给下游。",
                "選擇宿主登記的 HTTP 連線並設定逾時。以上游訊息觸發 GET，將回傳文字或 JSON 傳至下游。"),
            new("AgentFlow tool adapter scheduled by MAF. Connection policy and HTTP access are enforced by the host.",
                "由 MAF 调度的 AgentFlow 工具适配器。连接策略和 HTTP 访问由宿主执行。",
                "由 MAF 排程的 AgentFlow 工具配接器。連線策略與 HTTP 存取由宿主執行。")),

        NodeTypes.FileRead => new(
            new("Enter a relative path under the host’s allowed tool directory and connect a trigger. The node outputs UTF-8 text.",
                "填写宿主授权工具目录内的相对路径并连接触发消息。节点输出 UTF-8 文本。",
                "填寫宿主授權工具目錄內的相對路徑並連接觸發訊息。節點輸出 UTF-8 文字。"),
            new("AgentFlow file adapter scheduled by MAF. The host controls path access and file limits.",
                "由 MAF 调度的 AgentFlow 文件适配器。宿主控制路径访问和文件上限。",
                "由 MAF 排程的 AgentFlow 檔案配接器。宿主控制路徑存取和檔案上限。")),

        NodeTypes.SqliteQuery => new(
            new("Select a registered source, choose columns and optionally add equality filters. The read-only query returns a JSON array.",
                "选择已登记的数据源和结果列，需要时添加等值筛选。只读查询返回 JSON 数组。",
                "選擇已登記的資料來源和結果欄，視需要加入等值篩選。唯讀查詢傳回 JSON 陣列。"),
            new("AgentFlow SQLite adapter scheduled by MAF. The host builds the restricted read-only query.",
                "由 MAF 调度的 AgentFlow SQLite 适配器。受控只读查询由宿主生成并执行。",
                "由 MAF 排程的 AgentFlow SQLite 配接器。受控唯讀查詢由宿主產生並執行。")),

        NodeTypes.McpTool => new(
            new("Choose a registered MCP server and tool. Use Discover tools to inspect its inputs; set arguments as JSON or use the incoming JSON.",
                "选择已登记的 MCP 服务和工具。用「发现工具」查看输入，可填写 JSON 参数或使用输入消息。",
                "選擇已登記的 MCP 伺服器和工具。使用「探索工具」查看輸入，可填寫 JSON 參數或使用輸入訊息。"),
            new("AgentFlow schedules the direct tool call as one MAF executor. The MCP client and host authorization perform the call.",
                "AgentFlow 将直连工具调用作为一个 MAF 执行器调度。MCP 客户端和宿主授权机制处理调用。",
                "AgentFlow 將直接工具呼叫作為一個 MAF 執行器排程。MCP 用戶端及宿主授權機制處理呼叫。")),

        NodeTypes.JsonTransform => new(
            new("Connect JSON and enter a pointer such as /review/state. Use text to unwrap strings, or json to keep their JSON representation.",
                "接入 JSON，填写例如 /review/state 的指针。格式 text 可去掉字符串的 JSON 引号，json 保留 JSON 表示。",
                "接入 JSON，填寫例如 /review/state 的指標。格式 text 可去掉字串的 JSON 引號，json 保留 JSON 表示。"),
            new("AgentFlow data adapter operated as a MAF executor. Pointer evaluation and output formatting are implemented locally.",
                "由 MAF 调度的 AgentFlow 数据适配器。指针取值和输出格式由项目实现。",
                "由 MAF 排程的 AgentFlow 資料配接器。指標取值與輸出格式由專案實作。")),

        NodeTypes.Subworkflow => new(
            new("Encapsulate a selected subgraph or connect an existing call. Double-click the title to enter the locked revision. Its public text ports take input and return a result.",
                "封装选中的子图或连接现有调用。双击标题进入锁定版本。公开文本端口接收输入并返回结果。",
                "封裝選取的子圖或連接現有呼叫。按兩下標題進入鎖定版本。公開文字連接埠接收輸入並傳回結果。"),
            new("Related to MAF Sub-workflows. AgentFlow currently creates a separate MAF execution for each call and manages revision locking.",
                "对应 MAF 子工作流。当前 AgentFlow 为每次调用启动独立的 MAF 执行，并管理版本锁定。",
                "對應 MAF 子工作流程。目前 AgentFlow 為每次呼叫啟動獨立的 MAF 執行，並管理版本鎖定。"), Subworkflow),

        NodeTypes.Approval => new(
            new("Connect the step to review and configure the assignee, instructions and deadline. Approving forwards the original input; rejection ends the run.",
                "连接待审核步骤，填写处理人、说明和截止时限。批准后继续传递原输入，拒绝则结束本次运行。",
                "連接待審核步驟，填寫處理人、說明與截止時限。核准後傳遞原始輸入，拒絕則結束此次執行。"),
            new("Uses MAF RequestPort and Checkpoint. AgentFlow manages the approval form, assignee and timeout for a serial root workflow.",
                "使用 MAF RequestPort 和 Checkpoint。AgentFlow 为串行根工作流管理审批表单、处理人和超时。",
                "使用 MAF RequestPort 和 Checkpoint。AgentFlow 為串行根工作流程管理審批表單、處理人與逾時。"), Human),

        NodeTypes.UserInput => new(
            new("Set an assignee, instructions and deadline. When the workflow waits, submit text in the human input panel. The reply becomes the next step’s input.",
                "设置处理人、说明和等待时限。进入等待后在人工输入面板提交文本，答复成为下一步输入。",
                "設定處理人、說明與等待時限。開始等待後在人工輸入面板提交文字，回覆成為下一步輸入。"),
            new("Uses MAF RequestPort and Checkpoint. AgentFlow presents the form and passes the external text response to the workflow.",
                "使用 MAF RequestPort 和 Checkpoint。AgentFlow 提供表单，将外部文本答复交回工作流。",
                "使用 MAF RequestPort 和 Checkpoint。AgentFlow 提供表單，將外部文字回覆交回工作流程。"), Human),

        NodeTypes.SimulatedWrite => new(
            new("This example step follows Approval. Enable simulated writes in the host to run it. The output identifies the local receipt.",
                "示例步骤接在审批之后。在宿主启用模拟写入后可运行，输出中可看到本地回执标识。",
                "範例步驟接在審批後方。在宿主啟用模擬寫入後可執行，輸出會提供本機回執識別碼。"),
            new("AgentFlow simulation executor scheduled by MAF. Replays use a host-side receipt keyed by run and node.",
                "由 MAF 调度的 AgentFlow 模拟执行器。重复恢复按运行和节点查找宿主保存的回执。",
                "由 MAF 排程的 AgentFlow 模擬執行器。重複復原時依執行及節點查詢宿主保存的回執。")),

        NodeTypes.Normalize => new(
            new("Connect the text to clean. Leading and trailing whitespace is removed before forwarding the result.",
                "接入待处理文本。节点移除首尾空白，再将结果传给下游。",
                "接入待處理文字。節點移除前後空白，再將結果傳至下游。"),
            new("Deterministic AgentFlow text adapter scheduled by MAF.",
                "由 MAF 调度的确定性 AgentFlow 文本适配器。",
                "由 MAF 排程的決定性 AgentFlow 文字配接器。")),

        NodeTypes.Probe => new(
            new("Use this editor sample to add or remove ports and inspect their types and connection rules.",
                "用此编辑器样例增减端口，查看端口类型和连接规则。",
                "使用此編輯器範例增減連接埠，查看型別和連線規則。"),
            new("AgentFlow editor test node. The node has no MAF execution binding.",
                "AgentFlow 编辑器试验节点，未绑定 MAF 执行。",
                "AgentFlow 編輯器測試節點，尚未繫結 MAF 執行。")),

        _ => throw new GraphError("UnknownNodeType", typeId)
    };
}
