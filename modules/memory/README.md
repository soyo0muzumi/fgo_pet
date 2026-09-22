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

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`
- **禁止**：dialogue（方向相反：dialogue 消费 memory 的公开契约）；`host`

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`memory_candidates` `memories`（2 张）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

记忆是**用户数据**，可能含私密对话派生内容。复核与删除必须鉴权；持久化受保护；日志不得出现记忆正文。

## Development and validation

Core / App / Infrastructure / Windows 的 memory 相关测试；dialogue↔memory 契约测试；边界变更需 release 构建。

## 要点 / 易错处

1. 改动本模块的 README 时注意：v1 README 曾把 `conversation summaries` 列进 Responsibilities，**那是越界声明，已删除**。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q5 **Q7** —— 完整论证见工作区 `modules-v2/memory.md`
