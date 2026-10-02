# memory

> 拥有「已确认的用户记忆」。**不拥有对话摘要**（摘要归 dialogue）。

## Responsibilities

- 记忆候选、复核、确认
- 启用/禁用、删除
- 已确认记忆的查询与持久化
- `MemorySettings` / `IMemorySettingsStore`：记忆启用状态

## Non-responsibilities

- **对话摘要**（`ConversationSummaryService` 在纯 Kernel）
- 对话编排、提示词组装

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用遵循当前 `tests/architecture/policy.json`；精确遗留例外只允许减少。

`IMemoryRecall.Query` 每请求读取最新 revision，按角色通用/当前项目范围及查询词裁选完整条目。`IMemoryCandidateSink.Begin/Stage/Abandon` 提供带 generation、revision 和原文来源的候选写入端口，不提供审批、修改或删除能力。`IConversationMemory` 仅保留兼容读取适配，不再暴露候选写入。管理操作统一由 `MemoryCandidateService` 处理。

候选只落 Pending；审批更正必须匹配准确 ID、同一 scope 和 expected version。来源与规范化正文分别去重；删除回执阻止同源重建。每角色 200 条 / 40,000 字符含停用记录，旧数据超额时仍保留。`IMemoryWriteLifetime.StartSession` 在启动恢复和数据库迁移后旋转代次，旧 pending 作业不恢复执行。Memory 本身不发模型请求，也不反向引用 Dialogue。

## Dependencies

纯 Memory 能力、存储与 extraction 队列由 Memory.Core 拥有；管理 UI 与 Provider extractor 由 Memory desktop 拥有。UI 使用 UiSdk 发布的隐私请求端口，不再依赖 HostContracts；DataManagement 执行隐私策略和流程。Kernel 提供原消息来源及对话生命周期端口，Memory 不依赖 Dialogue 的实现或渲染器。

- **允许**：`src/FgoPet.Platform/*`、`src/FgoPet.UiSdk/*`
- **禁止**：依赖 Dialogue 或 Desktop 实现；Dialogue 只能消费 Memory 的公开契约。

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`memory_candidates` `memories` `memory_write_state` `memory_ingestions`

仓储只写本模块的表。来源所属角色、项目、原文版本由 Dialogue 验证；Memory 的来源存在性查询仅用于 FK/归属完整性复核和“来源已删除”状态，不读取或检索历史正文。项目名称保存在来源快照中，管理页不从 Dialogue 查询会话列表。

## Data and security boundaries

记忆是**用户数据**，可能含私密对话派生内容。复核与删除必须鉴权；持久化受保护；日志不得出现记忆正文。

## Development and validation

Core / App / Infrastructure / Windows 的 memory 相关测试；dialogue↔memory 契约测试；边界变更需 release 构建。

记忆管理呈现的回归入口：`MemoryViewModelTests` 与 `MemoryContextIntegrationTests`。覆盖角色范围、刷新代次、失败/重试及宿主订阅释放；查询失败不能显示为“没有数据”。会话列表、继续与单条删除由 dialogue 的历史入口管理，memory 不再查询或缓存会话列表。

## 要点 / 易错处

1. 改动本模块的 README 时注意：v1 README 曾把 `conversation summaries` 列进 Responsibilities，**那是越界声明，已删除**。

## Validation

Use the Memory Core/Desktop test projects under `tests/`, plus affected application tests and the architecture gate for boundary changes. Privacy, source ownership, generation rotation, and failure/retry behavior must remain covered.

`MemoryWebPage` owns the shared Settings Web module over `MemoryViewModel`. The former ConversationMemoryPage XAML is removed; role context, review, confirmation, and data policies retain their existing owners.
