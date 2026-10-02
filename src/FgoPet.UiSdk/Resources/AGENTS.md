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
- 模块对外只暴露已发布的 UI SDK 契约；依赖规则见 `tests/architecture/policy.json`，临时例外见只允许缩减的 `tests/architecture/baseline.json`。

## Safety invariants

资源不得内嵌密钥、提示词、用户数据或业务状态；需保持可访问对比度、键盘行为与确定性的主题选择。

## Minimum validation

主题/资源/通用控件测试 + Windows 测试；共享视觉边界变更需 release 构建与相关集成覆盖。

## 决策出处

资源与共享控件变更需遵循本文件的最低验证要求。
