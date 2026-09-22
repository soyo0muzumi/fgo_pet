# ui-foundation

> 可复用 WPF 令牌、主题、控件、图标与行为。**零业务语义**。

## Responsibilities

- 设计令牌与主题（`ThemeTokens` / `AppTheme` / `Themes/*`）
- 通用控件与图标（`Controls` / `SettingsControls` / `SettingsIcons` / `Shell*`）
- `ThemeService`（§6.8）
- `ThemeSettings` / `IThemeSettingsStore`：主题设置分区的公开契约

## Non-responsibilities

- **面向用户的文案**（Q1/Q8）
- 任何业务状态或业务语义控件

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

## Dependencies

- **允许**：WPF/框架库
- **禁止**：**任何业务模块、host**

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

（无表）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

资源不得内嵌密钥、提示词、用户数据或业务状态；需保持可访问对比度、键盘行为与确定性的主题选择。

## Development and validation

主题/资源/通用控件测试 + Windows 测试；共享视觉边界变更需 release 构建与相关集成覆盖。

## 要点 / 易错处

1. `AppTheme` 与 `ThemeService` 的**双向耦合**已按 §6.8 收口：两者同住本层，不再跨层互指。
2. `Ui/Shell/*` 三个文件从 `src/FgoPet.App` 迁入本层。其 `pack://` URI 由 csproj 的 `Link` 固定为原 App 相对路径（`Ui/Shell/...`），故消费方 XAML 无需改动。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 **Q8** **§6.8** —— 完整论证见工作区 `modules-v2/ui-foundation.md`
