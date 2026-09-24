# memory

> 拥有「已确认的用户记忆」。**不拥有对话摘要**（摘要归 dialogue）。

## Responsibilities

- 记忆候选、复核、确认
- 启用/禁用、删除
- 已确认记忆的查询与持久化
- `MemorySettings` / `IMemorySettingsStore`：记忆启用状态

## Non-responsibilities

- **对话摘要**（`ConversationSummaryService` 归 dialogue）
- 对话编排、提示词组装

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

`IMemoryRecall.Query` 每请求读取最新 revision，按角色通用/当前项目范围及查询词裁选完整条目。`IMemoryCandidateSink.Begin/Stage/Abandon` 提供带 generation、revision 和原文来源的候选写入端口，不提供审批、修改或删除能力。`IConversationMemory` 仅保留兼容读取适配，不再暴露候选写入。管理操作统一由 `MemoryCandidateService` 处理。

候选只落 Pending；审批更正必须匹配准确 ID、同一 scope 和 expected version。来源与规范化正文分别去重；删除回执阻止同源重建。每角色 200 条 / 40,000 字符含停用记录，旧数据超额时仍保留。`IMemoryWriteLifetime.StartSession` 在启动恢复和数据库迁移后旋转代次，旧 pending 作业不恢复执行。Memory 本身不发模型请求，也不反向引用 Dialogue。

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`
- **禁止**：dialogue（方向相反：dialogue 消费 memory 的公开契约）；`host`

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

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

`src/FgoPet.Memory/FgoPet.Memory.csproj` 已独立编译本模块的应用和桌面呈现等条目。部分契约、仓储与兼容依赖仍经 legacy Core / Infrastructure 提供；这不代表目标依赖方向已全部收口。

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 后续收口以实际契约和依赖方向为准，不重复创建已有工程。

## Migration debt

- legacy 引用及批量数据管理呈现的历史耦合仍需按所有权逐项收口。
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q5 **Q7** —— 完整论证见工作区 `modules-v2/memory.md`
