# focus Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

拥有计时状态机与完成账本。**显示快照 ≠ 业务事实**。

本模块拥有：

- 计时状态机、预设、阶段与状态转换
- 完成账本（羁绊进度、时间线条目）
- `RuntimeEventType` 常量集（Q9）

## Boundaries and dependencies

- **允许依赖**：`platform/*`、`ui-foundation/*`
- **禁止依赖**：dialogue、`host`
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

计时是**长运行状态**：崩溃/休眠恢复后不得丢账或重复记账。显示快照不得被当作权威事实回写。

## Minimum validation

Core / App / Infrastructure / Windows 的 focus / bond / timeline 测试；状态机转换与恢复需专项覆盖。

## 决策出处

Q1 Q2 **Q9** **Q10**（详见工作区 `modules-v2/focus.md`）
