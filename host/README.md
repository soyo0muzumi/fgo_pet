# host

> DesktopShell + DataManagement。**只装配，不存业务状态**。

## Responsibilities

- DesktopShell：应用启动与生命周期、服务装配、托盘、窗口与几何、设置外壳、附加面板与立绘窗的呈现编排
- DesktopShell：`AttachedPanelState` / `AttachedPanelStateMachine`（纯 UI 容器状态机，Q10）
- DesktopShell：`Desktop/DialogueWindow.xaml` 及其代码后置，组合 Dialogue 的聊天呈现与 Work 的独立待办工作区，并路由专注、设置及待办定位
- DataManagement：私有备份/恢复、导出、清理的**具名应用流程**

私有恢复由 `PendingBackupRestoreService` 校验并暂存副本，界面随后请求正常退出。DesktopShell 持有数据目录的独占文件锁，在下一次启动的主题、窗口、计时与轮询服务创建前执行恢复；`CompleteStartup` 后维护协调器拒绝直接换库。回滚失败或发现中断标记时保留原始回滚文件并阻止常规启动，不能返回“已回滚”。
- Settings：拥有唯一 schema-v2 codec 与进程内读-改-写协调器，实现各所有者发布的设置端口
- 设置外壳只保存导航分组、最近的能力子页和各页面的滚动位置；页面草稿仍由原有模块 ViewModel 持有。侧栏通过 DesktopAppUi 返回现有聊天、待办或专注界面。

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

聊天/待办窗口外壳由 `FgoPet.DesktopShell` 编译，仍使用原有构造函数及宿主工厂，不保存第二份会话或待办状态。`ConversationViewModel`、`DialogueWindowViewModel` 和聊天卡片由 Dialogue 编译；独立待办工作区和业务写入仍由 Work 编译。窗口保留 `FgoPet.App.Dialogue.DialogueWindow` 类型名称与 `Dialogue/DialogueWindow.xaml` 资源 Link，完整 pack URI 的程序集段为 `FgoPet.DesktopShell`。程序集归属变化必须完整重建、部署，不能只替换单 DLL。

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

DataManagement 可触**全部 27 张**，但**仅经维护门禁**；DesktopShell 零业务表

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

DataManagement 是**最高危路径**：备份/恢复/清理必须过维护门禁与地震（quiesce）；异常必须脱敏后才落盘；删除必须覆盖全部 7 类表。

## Development and validation

Windows / EndToEnd 的 shell 与生命周期测试；备份/恢复/导出/清理各需独立的往返验证；装配变更需 release 构建。

`MemoryContextIntegrationTests` 验证数据管理页随活动角色更新，以及后台通知经 UI Dispatcher 投影、宿主释放后不再更新。当前 `MemoryContextConnector` 与 ServiceRegistration 一起由 App 组合入口编译；它不保存另一份活动角色，也不增加模块工程之间的引用。

`DialogueWindowIntegrationTests` 继续验证真实窗口、聊天/待办切换、待办定位及收起重开。`WorkCardCompositionTests` 检查迁移后的 BAML 唯一归属、卡片加载、用户确认与路由事件；`DialogueWorkPresentationBoundaryTests` 禁止 Dialogue 经项目引用、编译产物或 XAML 重新依赖 Work/宿主实现。

## 要点 / 易错处

1. **Host 也适用模块的 8 目录骨架**（`Domain/` 承载 UI 容器状态机）。设计 §7 只说「每个模块内部采用相同的职责布局」，没有禁止 Host 有 `Domain/`。
2. `Theming/ThemeService.cs` 归 `ui-foundation`（§6.8）、`Themes/*` 与共享控件同样归 `ui-foundation`——**不在 host 下**。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

DesktopShell、HostContracts、DataManagement、SettingsHost 已有独立工程。App 的启动与服务组合仍由 `FgoPet.App` 编译；部分兼容契约和基础设施仍来自 legacy 工程。不得据此宣称所有目标架构约束已经完成。

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4；当前聊天/待办窗口以上述宿主编译归属为准。
- 后续收口以实际契约和依赖方向为准，不重复创建已有工程。

## Migration debt

- legacy 引用和组合入口仍需逐项按目标边界收口；窗口迁移保留既有模块用例/呈现对象的构造注入，不代表宿主已全面改用模块注册入口。
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 Q2 Q3 Q5 **Q6** **Q8** **Q10** —— 完整论证见工作区 `modules-v2/host.md`
