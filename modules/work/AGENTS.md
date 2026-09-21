# work Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

Todo + Execution + Archives 同一一致性边界。**不建 WorkManager**。

本模块拥有：

- Todo：条目/步骤/优先级、日期规则、编辑规则、待确认提案、**幂等创建**
- Execution：派发、执行记录、授权检查、回执归一、停止/未知结果/重连、项目快照
- Archives：工作归档草稿、确认、长期归档

## Boundaries and dependencies

- **允许依赖**：`platform/*`、`ui-foundation/*`、`adapters/agent-integration`（仅窄派发契约）
- **禁止依赖**：dialogue（方向相反）、`host`
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

派发前必须过授权检查；执行记录与归档不得写入未脱敏的异常全文；**幂等键必须真正幂等**（重复确认不得产生第二条记录）。

## Minimum validation

Core / App / Infrastructure / Windows 的 todo / execution / archive 测试；work↔agent-integration 派发边测试；幂等与恢复语义需专项回归。

## 决策出处

Q1 **Q2** **Q4** Q5 Q7 **Q10** **草稿归属** **提案边界（发布侧）**（详见工作区 `modules-v2/work.md`）
