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

- **允许依赖**：`platform/*`、`ui-foundation/*`
- **禁止依赖**：dialogue（方向相反：dialogue 消费 memory 的公开契约）、`host`
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

记忆是**用户数据**，可能含私密对话派生内容。复核与删除必须鉴权；持久化受保护；日志不得出现记忆正文。

## Minimum validation

Core / App / Infrastructure / Windows 的 memory 相关测试；dialogue↔memory 契约测试；边界变更需 release 构建。

## 决策出处

Q1 Q2 Q5 **Q7**（详见工作区 `modules-v2/memory.md`）
