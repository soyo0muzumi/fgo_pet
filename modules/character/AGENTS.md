# character Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

拥有角色包与当前角色唯一状态。**面板与 Dialogue 不得维护第二份**。

本模块拥有：

- 角色包安装、校验、索引、目录
- 当前角色/外观的唯一状态
- 人格、知识、外观内容的读取契约
- 内容绑定（`content_bindings` 表）

## Boundaries and dependencies

- **允许依赖**：`platform/*`、`ui-foundation/*`、`focus`（仅公开事实）
- **禁止依赖**：dialogue（方向相反）、`host`
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

角色包是**外部输入**：必须做完整性/版本校验（`SemVersion`、`AppearanceValidator`），解包不得逃逸目标目录（zip-slip）。偏好数据属用户数据。

## Minimum validation

Core / App / Infrastructure / Windows 的 packs / portraits / servants 测试；包安装与校验需专项覆盖（含恶意包用例）。

## 决策出处

Q1 Q2 Q3 **Q7** **Q12**（详见工作区 `modules-v2/character.md`）
