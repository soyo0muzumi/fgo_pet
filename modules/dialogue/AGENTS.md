# dialogue Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

产品主入口。拥有对话运行过程；其他模块拥有自己的业务事实。

本模块拥有：

- 会话与消息、流式过程
- 提示词组装、预算、注入拦截
- 结构化输出校验、工具调用聚合
- 模型连接用例、会话摘要

## Boundaries and dependencies

- **允许依赖**：`platform/*`、`ui-foundation/*`，以及**明确允许**的 memory / character / speech / work 的 `Contracts`
- **禁止依赖**：其他模块的 `Application` / `Infrastructure` / `Desktop`、`host`
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

模型输出是**不可信输入**：入站必须过 `PromptInjectionGuard` 与 schema 校验。不得把凭据、原始异常全文或未脱敏的用户数据写入提示词或日志。

## Minimum validation

Core / App / Windows 的 dialogue 相关测试；跨模块边（dialogue→memory / character / speech / work）的契约测试；改动提示词或预算常量需跑 EndToEnd。

## 决策出处

Q1 Q2 Q5 **Q7** **提案边界（机制侧）**（详见工作区 `modules-v2/dialogue.md`）
