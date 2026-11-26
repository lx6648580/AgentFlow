# AgentFlow Studio 术语

## MAF 节点与编排命名

以下为 Phase 07 对齐目标。英文保留 MAF 文档名称或准确的 .NET 类型名。简体中文和繁体中文采用项目统一译法。官方中文页面出现多种译法时，可在搜索和帮助中使用常见别名，界面标题使用表中译名。

| English | 简体中文 | 繁體中文 | 对应概念 |
|---|---|---|---|
| `ChatClientAgent` | 聊天客户端智能体 | 聊天用戶端代理程式 | `Microsoft.Agents.AI.ChatClientAgent` |
| `HarnessAgent` | Harness 智能体 | Harness 代理程式 | `Microsoft.Agents.AI.HarnessAgent`。搜索可接受 Microsoft Learn 的「工具套件代理」 |
| Executor | 执行器 | 執行器 | MAF 调度的执行单元 |
| Agent Executor | 智能体执行器 | 代理程式執行器 | 原生 Agent 绑定的执行器 |
| Conditional Edge | 条件边 | 條件邊 | 带条件的 Workflow 边 |
| Switch-Case | 多路分支 | 多路分支 | 多路条件边，.NET 装配入口为 `AddSwitch` |
| Fan-out | 扇出 | 扇出 | 将一份消息分发给多个目标 |
| Fan-in | 扇入 | 扇入 | 多个来源向同一目标汇入 |
| Fan-in Barrier | 扇入屏障 | 扇入屏障 | 等待约定的全部来源，映射 `AddFanInBarrierEdge` |
| Request Port | 请求端口 | 請求連接埠 | MAF `RequestPort` |
| Human-in-the-loop | 人工干预 | 人工介入 | 基于请求与响应的人工交互 |
| Approval | 人工审批 | 人工審批 | AgentFlow 对请求端口提供的用途封装 |
| User Input | 人工输入 | 人工輸入 | AgentFlow 对请求端口提供的用途封装 |
| Sub-workflow | 子工作流 | 子工作流程 | MAF 子工作流，版本选择由宿主管理 |
| Checkpoint | 检查点 | 檢查點 | 运行策略与诊断状态 |
| Handoff | 交接 | 交接 | 多智能体编排模板。搜索可接受「转接」「移交」 |
| Input / Output | 输入 / 输出 | 輸入 / 輸出 | 工作流边界。文本输入节点可写作 Text Input |
| Adapter | 适配器 | 配接器 | AgentFlow 或插件提供的自定义步骤 |

Agent 类型保留原名作为搜索词和类型标识提示。条件、扇出、扇入等名称用于语义相符的图元。聚合或拼接数据的步骤使用 Merge 等具体名称，并标明适配器来源。旧节点按固定 `typeId` 和 `version` 解析，显示名称、翻译和搜索别名单独维护。

[MAF 智能体管道](https://learn.microsoft.com/zh-cn/agent-framework/agents/agent-pipeline) · [Harness](https://learn.microsoft.com/zh-cn/agent-framework/concepts/harness) · [Workflow 边](https://learn.microsoft.com/zh-cn/agent-framework/concepts/workflows/edges) · [人工干预](https://learn.microsoft.com/zh-cn/agent-framework/workflows/human-in-the-loop) · [交接编排](https://learn.microsoft.com/zh-cn/agent-framework/workflows/orchestrations/handoff)

## 通用界面及现有节点术语

| English | 简体中文 | 繁體中文 |
|---|---|---|
| Workflow | 工作流 | 工作流程 |
| Canvas | 画布 | 畫布 |
| Node | 节点 | 節點 |
| Port | 端口 | 連接埠 |
| Link | 连线 | 連線 |
| Node library | 节点库 | 節點庫 |
| Group | 分组 | 群組 |
| Undo / Redo | 撤销 / 重做 | 復原 / 取消復原 |
| Draft | 草稿 | 草稿 |
| Minimap | 小地图 | 小地圖 |
| Chat model | 聊天模型 | 聊天模型 |
| Agent | 智能体 | 代理程式 |
| Prompt | 提示词 | 提示詞 |
| Condition | 条件 | 條件 |
| Parallel | 并行 | 平行 |
| Merge | 合并 | 合併 |
| Debug | 调试 | 偵錯 |
| Bypass / Disabled | 旁路 / 禁用 | 略過 / 停用 |

工作流 ID、节点 ID、类型 ID、版本、端口 ID、用户自定义名称、参数值与输入内容沿用文件中的原值。切换界面语言更新显示资源。
