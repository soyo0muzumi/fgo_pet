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
- **禁止依赖**：任何业务模块或 Desktop 表现层
- 模块对外只暴露 `Contracts/`；其他模块只能经明确允许的契约边访问本模块。规则见 `tests/architecture/policy.json`；临时例外见只允许缩减的 `tests/architecture/baseline.json`。

## Safety invariants

凭据走 Windows Credential Manager（`WindowsCredentialStore`）；受保护配对状态保持原 DPAPI 边界，不得明文落盘；迁移必须可重入且失败不半途留脏；诊断不得输出用户数据。

## Minimum validation

Core / Infrastructure 测试；迁移需在**空库与已迁移库**两种起点验证；凭据实现改动需 Windows 测试。

## 决策出处

最低验证要求见本文件及对应测试项目；架构边界变更需运行架构门禁。
