# platform Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

跨切技术层：SQLite 原语与迁移、运行时事件、诊断、设置基础、凭据、几何、窗口放置。**零业务语义**。

本模块拥有：

- 数据库连接、事务、迁移（`RuntimeDatabase` / `RuntimeDatabaseMigrator`）与 `schema_migrations` 表
- `RuntimeEvent` / `RuntimeEventSource` 记录类型
- 诊断接口、凭据端口与 Windows 实现、几何与窗口放置、设置基础类型

## Boundaries and dependencies

- **允许依赖**：无（本层是依赖链的末端之一）
- **禁止依赖**：**任何业务模块、host、ui-foundation 的表现层**
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

凭据走 DPAPI（`WindowsCredentialStore`），不得明文落盘；迁移必须可重入且失败不半途留脏；诊断不得输出用户数据。

## Minimum validation

Core / Infrastructure 测试；迁移需在**空库与已迁移库**两种起点验证；凭据实现改动需 Windows 测试。

## 决策出处

Q1 **Q2=b** **Q7** **Q9**（详见工作区 `modules-v2/platform.md`）
