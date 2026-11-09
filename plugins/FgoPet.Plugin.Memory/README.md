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

`IMemoryRecall.QueryStrict` 每次读取同一数据库快照中的最新 revision 和命中条目。普通聊天、Native 上下文和 `memory.search` 使用此路径，无匹配时返回空。`Query` 供记忆提取使用，无匹配时可返回最多三条角色通用已确认记忆。匹配优先考虑精确条目 ID，其次按 FTS5 相关性排序，短词使用字面匹配，结果保留原有整条记录与数量、字符上限。`IMemoryCandidateSink.Begin/Stage/Abandon` 提供带 generation、revision 和原文来源的候选写入端口，不提供审批、修改或删除能力。`IConversationMemory` 仅保留兼容读取适配，不再暴露候选写入。管理操作统一由 `MemoryCandidateService` 处理。

候选只落 Pending；审批更正必须匹配准确 ID、同一 scope 和 expected version。`IConversationMemoryReviewNotices` 在新增待审核候选提交后通知聊天，仅携带范围和会话标识。来源与规范化正文分别去重；删除回执阻止同源重建。每角色 200 条 / 40,000 字符含停用记录，旧数据超额时仍保留。`IMemoryWriteLifetime.StartSession` 在启动恢复和数据库迁移后旋转代次，旧 pending 作业不恢复执行。Memory 本身不发模型请求，也不反向引用 Dialogue。

自动写入待审核候选时，后续已排队的提取仍可依据同一记忆版本完成。提交时在同一事务内重新检查去重、容量和更正目标。已确认记忆发生变化或写入代次失效时，旧提取结果会被拒绝。Debug 日志记录暂存结果、来源哈希及候选数量。

候选提取可用时，「帮我记住」等明确请求会得到固定的审核流程指引。Native 正文阶段与普通聊天共用同一条指引。用户原话只用于判定是否适用，候选是否实际生成以应用提示为准。

## Dependencies

纯 Memory 能力、存储与 extraction 队列由 Memory.Core 拥有；管理 UI 与 Provider extractor 由 Memory desktop 拥有。UI 使用 UiSdk 发布的隐私请求端口，不再依赖 HostContracts；DataManagement 执行隐私策略和流程。Kernel 提供原消息来源及对话生命周期端口，Memory 不依赖 Dialogue 的实现或渲染器。

- **允许**：`src/FgoPet.Platform/*`、`src/FgoPet.UiSdk/*`
- **禁止**：依赖 Dialogue 或 Desktop 实现；Dialogue 只能消费 Memory 的公开契约。

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`memory_candidates` `memories` `memory_write_state` `memory_ingestions`

`memory_search` 是已启用记忆的派生 FTS5 索引，`memory_search_keys` 为外部内容视图提供跨备份压缩的稳定编号。数据库升级会回填索引，确认、编辑、停用、重新启用和删除在原写入事务中同步更新。`memories` 提供原文和来源元数据。

仓储只写本模块的表。来源所属角色、项目、原文版本由 Dialogue 验证；Memory 的来源存在性查询仅用于 FK/归属完整性复核和“来源已删除”状态，不读取或检索历史正文。项目名称保存在来源快照中，管理页不从 Dialogue 查询会话列表。

## Data and security boundaries

记忆是**用户数据**，可能含私密对话派生内容。复核与删除必须鉴权；持久化受保护；日志不得出现记忆正文。

## Development and validation

Core / App / Infrastructure / Windows 的 memory 相关测试；dialogue↔memory 契约测试；边界变更需 release 构建。

记忆管理呈现的回归入口：`MemoryViewModelTests` 与 `MemoryContextIntegrationTests`。覆盖角色范围、刷新代次、失败/重试及宿主订阅释放；查询失败不能显示为“没有数据”。会话列表、继续与单条删除由 dialogue 的历史入口管理，memory 不再查询或缓存会话列表。

## 要点 / 易错处

1. 改动本模块的 README 时注意：v1 README 曾把 `conversation summaries` 列进 Responsibilities，**那是越界声明，已删除**。

## Validation

`memory.search` and `NativeMemoryContext` query the existing confirmed-memory owner. They retain current role/project scope, enabled/reviewed filters, bounded output and source provenance. Context resolves the original user query through the protected query port and rechecks it before returning data; unavailable sources are reported as unavailable. Model/tool execution traces are never memory extraction evidence. Post-turn extraction continues to consume only authoritative user statements and validated final assistant messages.

Both native outputs retain memory ID, version and project scope, and expose `sourceKind`, `sourceOccurredAtUtc`, `sourceConversationId`, `sourceMessageId`, `sourceAvailable` and `confirmation: "UserReviewed"`. Confirmation describes the stored memory's user review; it does not turn an assistant suggestion into a user statement. Deleted source records keep their recorded origin with `sourceAvailable: false`. Legacy records without source metadata use `UnknownLegacy`, omit unknown origin fields and report source availability as false. Source fingerprints and project labels are not projected. Each request reads the owner's current revision, so corrections and disabled records take effect on the next query. Existing item, character and byte limits still apply to whole records, including the added metadata where the serialized payload is bounded.

Use the Memory Core/Desktop test projects under `tests/`, plus affected application tests and the architecture gate for boundary changes. Privacy, source ownership, generation rotation, and failure/retry behavior must remain covered.

`MemoryWebPage` owns the shared Settings Web module over `MemoryViewModel`. The former ConversationMemoryPage XAML is removed; role context, review, confirmation, and data policies retain their existing owners.

The Margin detail uses English localization keys and paper controls, with candidate review and confirmed memories in vertically stacked sections. Memory bodies and owner status render as plain text. Editing, approval, enable/disable and deletion retain the existing commands; delete-all retains its inline confirmation. `MarginMemoryDataWebTests` exercises production page assets with synthetic data and does not access private memories or perform real deletion. Owner and backup tests verify data policies separately.

关系与成长的“她记得的”Tab 保留 `ConversationMemory` 页面 ID 与 Memory 命令归属，默认显示已保存列表；候选逐条复核、编辑与更正对象选择、停用和删除仍由现有 owner 执行。记忆设置按需展开；切换 Tab 保留本机草稿，角色变化丢弃旧草稿；版本变化后阻止保存，需核对当前内容并明确重绑。单条删除与整体删除均先确认。验证入口包括 Windows `RelationshipV2WebTests`（临时合成数据库上的真实双 owner 与宿主）和 `MarginMemoryDataWebTests`。
