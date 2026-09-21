# ui-foundation Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

可复用 WPF 令牌、主题、控件、图标与行为。**零业务语义**。

本模块拥有：

- 设计令牌与主题（`ThemeTokens` / `AppTheme` / `Themes/*`）
- 通用控件与图标（`Controls` / `SettingsControls` / `SettingsIcons` / `Shell*`）
- `ThemeService`（§6.8）

## Boundaries and dependencies

- **允许依赖**：WPF/框架库
- **禁止依赖**：**任何业务模块、host**
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

资源不得内嵌密钥、提示词、用户数据或业务状态；需保持可访问对比度、键盘行为与确定性的主题选择。

## Minimum validation

主题/资源/通用控件测试 + Windows 测试；共享视觉边界变更需 release 构建与相关集成覆盖。

## 决策出处

Q1 **Q8** **§6.8**（详见工作区 `modules-v2/ui-foundation.md`）
