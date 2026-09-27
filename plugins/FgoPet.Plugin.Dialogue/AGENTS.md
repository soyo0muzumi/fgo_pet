# dialogue Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

产品主入口。拥有对话运行过程；其他模块拥有自己的业务事实。

本模块拥有：

- HTTP Provider 适配、请求协议策略与会话 SQLite 实现
- 模型连接设置及对话呈现
- 模块拥有的服务注册；宿主只负责跨模块端口与窗口组合

纯 .NET 对话编排、提示词预算、输出校验、工具聚合、摘要和原消息召回已迁入 Kernel，保留原命名空间与公开构造签名。原消息仍是权威数据；摘要与索引可重建。

## Boundaries and dependencies

- **允许依赖**：`src/FgoPet.Platform/*`、`src/FgoPet.UiSdk/Resources/*`，以及**明确允许**的 memory / character / speech / work 的 `Contracts`
- **禁止依赖**：其他插件的 `Application` / `Infrastructure` / `Desktop`、Desktop shell implementation
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（见当前 `tests/architecture/policy.json`；精确遗留例外只允许减少）。

## Safety invariants

模型输出是**不可信输入**：入站必须过 `PromptInjectionGuard` 与 schema 校验。不得把凭据、原始异常全文或未脱敏的用户数据写入提示词或日志。

## Minimum validation

运行 `tests/` 下 Dialogue、Kernel、Windows 和 EndToEnd 的相关测试；提示词、预算或 context 生命周期变更需覆盖 EndToEnd。

## 决策出处

依赖边界见 `tests/architecture/policy.json`，遗留例外见只允许缩减的 `baseline.json`。
