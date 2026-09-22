# host

> DesktopShell + DataManagement。**只装配，不存业务状态**。

## Responsibilities

- DesktopShell：应用启动与生命周期、服务装配、托盘、窗口与几何、设置外壳、附加面板与立绘窗的呈现编排
- DesktopShell：`AttachedPanelState` / `AttachedPanelStateMachine`（纯 UI 容器状态机，Q10）
- DataManagement：私有备份/恢复、导出、清理的**具名应用流程**
- Settings：拥有唯一 schema-v2 codec 与进程内读-改-写协调器，实现各所有者发布的设置端口

## Non-responsibilities

- 任何业务状态（Todo/记忆/专注/角色/对话）——**一行都不许有**
- 模块内部的业务规则与事务
- 各设置分区的业务语义或权威缓存

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

## Dependencies

- **允许**：模块的**注册入口**；`platform/*`、`ui-foundation/*`；`adapters/agent-integration`（运行时伴生）；speech 的表现层
- **禁止**：模块的仓储、状态实现或私有事件；业务 Lambda 不得写在 `ModuleRegistration` 里

Settings 组合模块契约，通过 platform 的不透明文档端口持久化；它不把业务设置状态重新定义在 host。

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

DataManagement 可触**全部 27 张**，但**仅经维护门禁**；DesktopShell 零业务表

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

DataManagement 是**最高危路径**：备份/恢复/清理必须过维护门禁与地震（quiesce）；异常必须脱敏后才落盘；删除必须覆盖全部 7 类表。

## Development and validation

Windows / EndToEnd 的 shell 与生命周期测试；备份/恢复/导出/清理各需独立的往返验证；装配变更需 release 构建。

## 要点 / 易错处

1. **Host 也适用模块的 8 目录骨架**（`Domain/` 承载 UI 容器状态机）。设计 §7 只说「每个模块内部采用相同的职责布局」，没有禁止 Host 有 `Domain/`。
2. `Theming/ThemeService.cs` 归 `ui-foundation`（§6.8）、`Themes/*` 与共享控件同样归 `ui-foundation`——**不在 host 下**。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

⚠️ **但程序集尚未拆分**：这些文件目前仍由 `src/FgoPet.{Core,Infrastructure,App}` 三个旧 csproj 通过 `<Compile Include>` / `<Page Include>` + `<Link>` 跨目录回链编译。**物理位置已是目标架构，程序集边界仍是 v1。**

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口动作：按模块拆分 csproj（**需单独授权**）

## Migration debt

- csproj 拆分未做（见上）
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q3 Q5 **Q6** **Q8** **Q10** —— 完整论证见工作区 `modules-v2/host.md`
