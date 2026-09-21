# focus

> 拥有计时状态机与完成账本。**显示快照 ≠ 业务事实**。

## Responsibilities

- 计时状态机、预设、阶段与状态转换
- 完成账本（羁绊进度、时间线条目）
- `RuntimeEventType` 常量集（Q9）

## Non-responsibilities

- 焦点事件的**角色反馈**（归 `character/Integrations/Focus`）
- 面板/窗口的呈现（归 `host/DesktopShell`）

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`
- **禁止**：dialogue；`host`

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`focus_presets` `focus_sessions` `runtime_events` `timeline_entries` `servant_bonds` `bond_ledger`（6 张）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

计时是**长运行状态**：崩溃/休眠恢复后不得丢账或重复记账。显示快照不得被当作权威事实回写。

## Development and validation

Core / App / Infrastructure / Windows 的 focus / bond / timeline 测试；状态机转换与恢复需专项覆盖。

## 要点 / 易错处

1. **表与实体归不同层**（§6.6 / §Q9）：`runtime_events` **表**归 focus，`RuntimeEvent` **record** 归 `platform/Runtime`，`RuntimeEventType` **常量**归 focus。
2. **`FocusDisplayText` 是本模块发布的只读展示投影**（卡 C 产物，`focus/Desktop`）。派生排版串不得搬进领域模型——`FocusSession` 不该认识 `"已暂停"`。
3. v1 README 里「generic runtime events 的最终归属待定」**已由 Q9 收口**，不再是债务。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 **Q9** **Q10** —— 完整论证见工作区 `modules-v2/focus.md`
