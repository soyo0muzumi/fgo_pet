# platform

> 跨切技术层：SQLite 原语与迁移、运行时事件、诊断、设置基础、凭据、几何、窗口放置。**零业务语义**。

## Responsibilities

- 数据库连接、事务、迁移（`RuntimeDatabase` / `RuntimeDatabaseMigrator`）与 `schema_migrations` 表
- `RuntimeEvent` / `RuntimeEventSource` 记录类型
- 诊断接口、凭据端口与 Windows 实现、几何与窗口放置、设置基础类型

## Non-responsibilities

- **任何业务规则或业务事件全集**（§4.3 / V3）
- **面向用户的文案**（Q1/Q8）
- 各模块的仓储（住在 `modules/*/Infrastructure/`）

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

## Dependencies

- **允许**：无（本层是依赖链的末端之一）
- **禁止**：**任何业务模块、host、ui-foundation 的表现层**

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`schema_migrations`（1 张）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

凭据走 DPAPI（`WindowsCredentialStore`），不得明文落盘；迁移必须可重入且失败不半途留脏；诊断不得输出用户数据。

## Development and validation

Core / Infrastructure 测试；迁移需在**空库与已迁移库**两种起点验证；凭据实现改动需 Windows 测试。

## 要点 / 易错处

1. **不得出现 `persistence/` 子层**（Q2=b）：仓储住在各业务模块内部，基建住在这里——两者不是同一层的两类东西。
2. **表与实体可归不同层**：`runtime_events` 表归 focus，`RuntimeEvent` record 归这里。
3. 平台层**只能保留业务中立的原语**；发现业务常量混进来（如 `RuntimeEventType`，已按 Q9 归 focus）应迁回业务模块。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 **Q2=b** **Q7** **Q9** —— 完整论证见工作区 `modules-v2/platform.md`
