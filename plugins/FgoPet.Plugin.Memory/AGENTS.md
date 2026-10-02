# memory Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

拥有「已确认的用户记忆」。**不拥有对话摘要**（摘要归 dialogue）。

本模块拥有：

- 记忆候选、复核、确认
- 启用/禁用、删除
- 已确认记忆的查询与持久化

## Boundaries and dependencies

- **允许依赖**：Platform、UiSdk 的发布端口及 Kernel 的原消息来源/对话生命周期契约
- **禁止依赖**：Dialogue（方向相反：Dialogue 消费 Memory 的公开契约）、Desktop shell implementation
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（见当前 `tests/architecture/policy.json`，精确遗留例外只允许减少）。

## Safety invariants

记忆是**用户数据**，可能含私密对话派生内容。复核与删除必须鉴权；持久化受保护；日志不得出现记忆正文。

## Minimum validation

运行 `tests/` 下 Memory Core/Desktop、Dialogue 和 Windows 的相关测试；边界变更需 Release 构建与架构门禁。

## 决策出处

依赖边界见 `tests/architecture/policy.json`，遗留例外见只允许缩减的 `baseline.json`。
