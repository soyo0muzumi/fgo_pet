# character

> 拥有角色包与当前角色唯一状态。**面板与 Dialogue 不得维护第二份**。

## Responsibilities

- 角色包安装、校验、索引、目录
- 当前角色/外观的唯一状态
- 人格、知识、外观内容的读取契约
- 内容绑定（`content_bindings` 表）

## Non-responsibilities

- 对话运行过程（归 dialogue）
- 专注状态本身（归 focus）——本模块只在其公开事实上做反馈

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`、`focus`（仅公开事实）
- **禁止**：dialogue（方向相反）；`host`

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`content_bindings`（1 张）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

角色包是**外部输入**：必须做完整性/版本校验（`SemVersion`、`AppearanceValidator`），解包不得逃逸目标目录（zip-slip）。偏好数据属用户数据。

## Development and validation

Core / App / Infrastructure / Windows 的 packs / portraits / servants 测试；包安装与校验需专项覆盖（含恶意包用例）。

## 要点 / 易错处

1. 原模块名 `servant-packs`，按设计 §3.5 **改名 `character`**（v1 目录已移除）。
2. `ServantFocusConnector` / `EventFeedbackSelector` 住在 `character/Integrations/Focus` —— 明确依赖 focus 公开事实的具名适配组件。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q3 **Q7** **Q12** —— 完整论证见工作区 `modules-v2/character.md`
