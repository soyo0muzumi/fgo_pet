# host Agent Rules

## Scope

本文件适用于本目录树，并继承仓库根 `AGENTS.md`。

## Ownership

DesktopShell + DataManagement。**只装配，不存业务状态**。

本模块拥有：

- DesktopShell：应用启动与生命周期、服务装配、托盘、窗口与几何、设置外壳、附加面板与立绘窗的呈现编排
- DesktopShell：`AttachedPanelState` / `AttachedPanelStateMachine`（纯 UI 容器状态机，Q10）
- DataManagement：私有备份/恢复、导出、清理的**具名应用流程**

## Boundaries and dependencies

- **允许依赖**：模块的**注册入口**；`platform/*`、`ui-foundation/*`；`adapters/agent-integration`（运行时伴生）；speech 的表现层
- **禁止依赖**：模块的仓储、状态实现或私有事件、业务 Lambda 不得写在 `ModuleRegistration` 里
- 模块对外只暴露 `Contracts/`；其他模块只能经**明确允许**的契约边访问本模块（全量 8 条见 `modules/README.md`）。

## Safety invariants

DataManagement 是**最高危路径**：备份/恢复/清理必须过维护门禁与地震（quiesce）；异常必须脱敏后才落盘；删除必须覆盖全部 7 类表。

## Minimum validation

Windows / EndToEnd 的 shell 与生命周期测试；备份/恢复/导出/清理各需独立的往返验证；装配变更需 release 构建。

## 决策出处

Q1 Q2 Q3 Q5 **Q6** **Q8** **Q10**（详见工作区 `modules-v2/host.md`）
