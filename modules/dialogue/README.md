# dialogue

> 产品主入口。拥有对话运行过程；其他模块拥有自己的业务事实。

## Responsibilities

- 会话与消息、流式过程
- 提示词组装、预算、注入拦截
- 结构化输出校验、工具调用聚合
- 模型连接用例、会话摘要

## Non-responsibilities

- 其他模块的业务状态（Todo/记忆/专注/角色）——只消费其公开契约
- 凭据存储本身（归 platform/Secrets）

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`，以及**明确允许**的 memory / character / speech / work 的 `Contracts`
- **禁止**：其他模块的 `Application` / `Infrastructure` / `Desktop`；`host`

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`conversations` `chat_messages` `conversation_summaries` `runtime_state`（4 张）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

模型输出是**不可信输入**：入站必须过 `PromptInjectionGuard` 与 schema 校验。不得把凭据、原始异常全文或未脱敏的用户数据写入提示词或日志。

## Development and validation

Core / App / Windows 的 dialogue 相关测试；跨模块边（dialogue→memory / character / speech / work）的契约测试；改动提示词或预算常量需跑 EndToEnd。

## 要点 / 易错处

1. **工具机制归本模块，工具定义不归**：`ChatToolDefinition` / `ChatToolCallDelta` / `ConversationRequest.Tools` 槽位是 dialogue 的；各模块自己的工具 schema 住在各模块 `Contracts/`（规则 `no-foreign-tool-schema`）。
2. `dialogue/Integrations/Work/` 承载 Todo 提案的 **JSON 解析 + 安全拦截半**；确认与写库半归 `work/Todo/Application`。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q5 **Q7** **提案边界（机制侧）** —— 完整论证见工作区 `modules-v2/dialogue.md`
