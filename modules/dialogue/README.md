# dialogue

> 产品主入口。拥有对话运行过程；其他模块拥有自己的业务事实。

## Responsibilities

- 会话与消息、流式过程
- 历史对话的标题、本地时间、继续与确认删除；删除时同步清理对应会话的运行状态，并避免与生成中的回复并发。
- 提示词组装、预算、注入拦截
- 结构化输出校验、工具调用聚合
- 模型连接用例、会话摘要
- `DialogueSettings` / `IDialogueSettingsStore`：模型连接元数据与推理展示偏好
- `Desktop/Cards`：聊天中的旧 Todo 建议卡片与归档草稿卡片呈现，经 Work 契约提交用户明确确认

## Non-responsibilities

- 其他模块的业务状态（Todo/记忆/专注/角色）——只消费其公开契约
- 凭据存储本身（归 platform/Secrets）
- 聊天、独立待办页、专注和设置之间的窗口组合（归 host/DesktopShell）；独立待办工作区仍归 Work

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

- `IConversationHistoryQuery.ReadPage` 返回会话标识、最多 50 字符的标题、更新时间与归档状态；每页最多 50 条，游标按角色隔离并保留数据库原始排序值。SQLite 用一次分页投影读取标题前缀，打开会话才读取完整消息。
- 对话通过 Memory 的 `IMemoryRecall` 读取当前范围和版本的记忆，通过 `IMemoryCandidateSink` 的来源票据提交待审候选；没有审批或删除权限。`ITodoDraftWorkflow` 仍由 Work 拥有，压缩前后完整再注入当前草稿。
- `IModelContextResolver` / `IRequestTokenMeter` / `PromptBudget` 统一主请求、历史定位、摘要和记忆提取的路由与预算。`IConversationRecallRepository` 按角色/项目发现与读取原文；`IConversationContextStore` 原子提交摘要及覆盖位置。原始消息不被压缩改写。
- `DialogueContextLifetime` 拥有请求和后台提取的取消、提交复核及维护期暂停；`MemoryExtractionQueue` 为每个作业持有独立 lease，容量 8、单消费者、每轮最多一次辅助模型调用。退出有界等待，迟到结果无写入 continuation。

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`，以及**明确允许**的 memory / character / speech / work 的 `Contracts`
- **禁止**：其他模块的 `Application` / `Infrastructure` / `Desktop`；`host`

`ConversationViewModel` 的旧 Todo 卡片入口消费 Work 的 `ILegacyTodoProposalPort`，归档入口消费 `IArchiveDraftConfirmation`，不持有 `TodoProposalService` 或 `ArchiveDraftService`。显示卡片不调用确认；旧卡片直接确认接口与模型提案端口互不继承，新对话继续使用 `ITodoDraftWorkflow` 的作用域、版本和幂等确认。契约所有权与兼容限制见 `../work/README.md`。

`TodoProposalCard`、`ArchiveDraftCard` 及其 ViewModel 由本模块编译；它们只拥有卡片编辑与反馈状态，提案解析、草稿版本、确认写入及归档清理仍归 Work。跨模块的 `DialogueWindow` 外壳由 DesktopShell 编译，组合现有对话 ViewModel 与 Work 的独立待办工作区。Dialogue 不再声明或绑定 `FgoPet.Work.Todo`、`FgoPet.Work.Execution`、`FgoPet.Work.Archives` 实现引用，也不反向引用 DesktopShell 实现。

卡片与窗口保留原类型命名空间、构造函数和资源 Link 路径，但程序集归属变化；需要完整重建、部署。卡片资源位于 `/FgoPet.Dialogue;component/Views/`，窗口资源位于 `/FgoPet.DesktopShell;component/Dialogue/DialogueWindow.xaml`。这不是整个 Dialogue 已满足目标依赖图的声明。

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`conversations` `chat_messages` `conversation_summaries` `conversation_contexts` `chat_message_search`（含 FTS5 辅助表）`runtime_state`

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

模型输出是**不可信输入**：入站必须过 `PromptInjectionGuard` 与 schema 校验。不得把凭据、原始异常全文或未脱敏的用户数据写入提示词或日志。

## Development and validation

Core / App / Windows 的 dialogue 相关测试；跨模块边（dialogue→memory / character / speech / work）的契约测试；改动提示词或预算常量需跑 EndToEnd。

`scripts/test-architecture.ps1` 是本地与 CI 共用的架构验证入口。`SqliteConversationRepositoryTests` 覆盖大历史分页与旧时间格式；`ConversationOrchestratorTests` 覆盖 Memory/Work 契约调用、完整草稿预算及过期请求；Windows 的复制反馈测试覆盖窗口生命周期。

`WorkCardBoundaryTests` 使用接口替身验证旧卡片与归档的零隐式写入、编辑、解析拒绝、移除和角色切换。`DialogueWorkPresentationBoundaryTests` 检查求值后的项目引用、真实 AssemblyRef、XAML 控件引用和类型唯一编译归属；缺少 Release 产物必须失败，不跳过。`WorkCardCompositionTests` 检查 BAML 唯一归属、新 pack URI 加载、编辑确认及事件冒泡。完整 Windows 和宿主装配测试继续覆盖实际窗口与独立待办页。

## 要点 / 易错处

1. **工具机制归本模块，工具定义不归**：`ChatToolDefinition` / `ChatToolCallDelta` / `ConversationRequest.Tools` 槽位是 dialogue 的；各模块自己的工具 schema 住在各模块 `Contracts/`（规则 `no-foreign-tool-schema`）。
2. Todo 提案的 JSON 解析与字段校验由 `work/Todo/Application/TodoProposalService` 实现，对话经 Work 契约调用；通用提示词防护与工具调用聚合仍归 Dialogue，确认及写库归 Work。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

`src/FgoPet.Dialogue/FgoPet.Dialogue.csproj` 已独立编译应用和桌面呈现。部分契约与仓储仍由 legacy Core / Infrastructure 编译；角色、记忆及语音的现存实现引用仍需收口，不能据此宣布模块完全独立。

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4；当前聊天卡片与窗口呈现归属以上述工程编译边界为准。
- 收口动作以实际调用链和公开契约为准，不重复创建已有工程。

## Migration debt

- 保留的 legacy、角色、记忆及语音实现依赖仍需逐项收口。
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q5 **Q7** **提案边界（机制侧）** —— 完整论证见工作区 `modules-v2/dialogue.md`
