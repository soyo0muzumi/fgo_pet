# 事件与状态管理架构指南

## 目的

本文定义 FGO Pet 中“命令、状态、事件和界面通知”的边界，解决角色激活、专注、立绘、对话、Agent 运行时和窗口之间的状态同步分散问题。

本文是后续重构的目标架构和迁移依据，不要求一次性重写现有模块。迁移期间允许保留兼容适配层，但新增代码应遵循本文边界。

## 核心规则

1. **命令表达意图，服务执行用例，状态保存事实，事件通知变化。**
2. 领域服务不直接调用 ViewModel，也不依赖 WPF 控件或 Dispatcher。
3. ViewModel 只负责界面绑定、输入校验和调用应用服务；跨模块业务状态不通过 ViewModel 互相传递。
4. `INotifyPropertyChanged` 只用于本 ViewModel 的界面刷新，不作为模块间业务协议。
5. 状态变化事件应携带新快照或足够的变化信息，订阅者不得依赖“收到事件后到处重新读取”来拼装状态。
6. 一次性用户请求使用命令或返回结果；只有需要多个消费者观察的长期状态才发布事件。
7. 事件订阅必须和对象生命周期配对，创建订阅的组件负责解除订阅。
8. 不引入全局 Event Bus。优先使用明确的服务接口、状态对象和局部协调器。

## 当前问题盘点

当前实现中存在以下状态来源和事件来源：

| 领域 | 当前来源 | 当前消费者 | 主要问题 |
| --- | --- | --- | --- |
| 当前角色 | `ServantFocusConnector._activeServantId`、设置存储、`PortraitController.CurrentState` | 专注面板、对话、反馈 | `servant_id` 没有统一所有者，启动和设置页激活路径不一致 |
| 角色/立绘激活 | `DesktopAppShell`、`ServantLibraryViewModel`、`RecentServantSwitcherViewModel` | 立绘窗口、面板 | 存在多条激活路径，部分路径不会同步全部上下文 |
| 专注会话 | `FocusSessionService.SnapshotChanged` | `AttachedPanelViewModel`、`ServantFocusConnector` | 一个快照事件被多个组件分别解释 |
| 立绘状态 | `PortraitController.StateChanged` | `PortraitWindowCoordinator`、`DesktopAppUi` | 状态刷新、窗口显示和恢复逻辑混在不同消费者中 |
| 对话 | `ConversationOrchestrator.Updated` | `ConversationViewModel` | 增量消息和会话状态没有统一的外部快照 |
| Agent 运行时 | `AgentRelaySnapshot` 和运行时事件 | Shell、连接设置、任务 ViewModel | 外部快照、投影和界面状态边界不清晰 |
| 托盘/窗口 | `TrayService` 的请求事件、WPF `PropertyChanged` | `DesktopAppUi`、窗口类 | UI 请求和应用状态通知混用 |

## 目标分层

```text
用户操作 / 外部输入
        |
        v
ViewModel 或基础设施适配器
        |
        v
应用服务（执行一个完整用例）
        |
        +--> 领域服务
        |
        +--> AppRuntime 状态切片（当前事实）
        |
        v
明确的状态变化事件 / 快照
        |
        v
协调器和 ViewModel
        |
        v
窗口、托盘和其他界面
```

### AppRuntime 与状态切片

应用级运行时只负责组合各模块状态和生命周期，不承担所有业务状态的集中写入。每个模块拥有自己的状态切片，避免形成无法维护的“上帝状态对象”：

```text
AppRuntime
├── RoleState
├── PortraitState
├── FocusState
├── ConversationState
├── AgentState
├── VoiceState
├── FileState
└── AppLifecycleState
```

`AppRuntime` 是当前会话状态的组合入口；每个状态切片的唯一写入者仍是对应模块。持久化设置仍由 `IAppSettingsStore` 管理；持久化设置不是实时运行状态，不能替代运行时状态。

## 可扩展模块

功能按“模块 + 适配器”扩展，不把新能力直接塞进现有 ViewModel：

```text
FgoPet.App
├── Core
│   ├── AppRuntime
│   ├── Command / Result
│   ├── Event / Snapshot
│   └── Module contracts
├── Modules
│   ├── Role
│   ├── Focus
│   ├── Portrait
│   ├── Conversation
│   ├── Todo
│   ├── Agent
│   ├── Voice
│   └── Files
├── Adapters
│   ├── Windows audio
│   ├── File system
│   ├── Relay / MCP
│   └── Model providers
└── UI
```

每个模块至少有自己的状态、命令、服务和事件边界：

```text
Voice
├── VoiceState
├── VoiceCommands
├── VoiceService
└── VoiceEvents
```

新增模块必须满足：

1. 不直接修改其他模块状态；
2. 不直接依赖其他模块的 ViewModel；
3. 外部设备、文件系统和网络服务通过 Adapter 隔离；
4. 能力不可用时只让本模块进入不可用状态，不阻塞应用启动；
5. 模块状态和服务可以在没有 WPF 窗口的测试环境中运行。

第一阶段只支持编译时注册的可选模块，不实现动态插件加载。模块契约可以预留为：

```csharp
public interface IFeatureModule
{
    string ModuleId { get; }
    void Register(IServiceCollection services);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

## 新能力接入示例

语音模块的边界：

```text
ConversationService
  → SpeechSynthesisService.Speak(text)
  → VoiceState
  → VoiceChanged / PlaybackCompleted
  → UI 更新播放状态
```

文件管理模块的边界：

```text
FileApplicationService
  → 导入 / 导出 / 打开 / 清理
  → FileOperationResult
  → FileChanged
  → 相关模块刷新自己的状态
```

文件系统适配器不能直接通知角色包页面，音频适配器也不能直接通知对话面板；中间必须经过所属模块服务。

## 目标组件职责

### RoleActivationService

统一处理所有角色激活入口：

- 根据 `PortraitSelection` 解析角色包和 `servant_id`；
- 调用立绘控制器完成激活；
- 更新 `AppRuntime` 中的 `RoleState` 和 `PortraitState`；
- 保存最后选择；
- 向调用方返回成功或结构化失败结果。

启动恢复、设置页激活、最近角色切换都必须最终调用此服务。

### FocusSessionService

只负责专注状态机和持久化：

- 接收开始、暂停、恢复、停止命令；
- 发布包含完整会话信息的 `FocusSnapshot`；
- 不解析角色包，不更新对白，不操作窗口。

### PortraitController

只负责立绘运行时：

- 激活肖像；
- 切换表情；
- 应用缩放和 DPI；
- 发布 `PortraitSnapshot`。

它不负责专注、对话、角色包设置页或托盘显示。

### FocusFeedbackCoordinator

由现有 `ServantFocusConnector` 迁移而来，只负责：

```text
FocusSnapshot + ActiveRole
        -> 对白选择
        -> 表情选择
        -> Conversation / Portrait 命令
```

它不再拥有 `_activeServantId` 这样的独立身份状态。

### AttachedPanelViewModel

只负责面板交互和展示：

- 从 `AppRuntime` 读取当前角色和专注状态；
- 根据状态计算 `CanStartFocus`、`CanPause` 等界面属性；
- 调用 `FocusApplicationService`；
- 将状态变化转换成本 ViewModel 的 `PropertyChanged`。

面板不再通过 `SetActiveServant` 接收其他模块推送的业务身份。

### DesktopAppShell / DesktopAppUi

- `DesktopAppShell`：编排启动、恢复和退出流程；
- `DesktopAppUi`：执行窗口、托盘和设置窗口显示；
- 二者都不解析角色身份，也不负责专注业务。

### AgentRuntime 与 Conversation

- Agent 适配器只负责外部 Relay/Adapter 协议和连接快照；
- 应用服务将外部快照投影为应用状态；
- 对话服务只负责对话会话和消息流；
- Todo 提案、Agent 派发和用户确认分别使用明确的命令与结果，不通过隐式事件串联。

## 模块间通信规则

```text
模块内部：直接调用 + 本地事件
模块之间：应用服务接口 + 类型化事件/快照
UI 内部：INotifyPropertyChanged
外部系统：Adapter Snapshot → 模块 State
```

跨模块事件只表达稳定的业务事实，例如 `ActiveRoleChanged`、`FocusChanged` 或 `PlaybackCompleted`。按钮点击、窗口显示和临时错误优先使用命令返回值，不新增全局事件。

## 事件分类

### 保留的状态变化事件

建议逐步统一为携带快照的事件：

- `ActiveRoleChanged(ActiveRoleSnapshot)`
- `PortraitChanged(PortraitSnapshot)`
- `FocusChanged(FocusSnapshot)`
- `ConversationChanged(ConversationSnapshot)`
- `AgentRuntimeChanged(AgentRuntimeSnapshot)`
- `AppLifecycleChanged(AppLifecycleSnapshot)`

事件只表示“事实已经更新”。事件处理失败不能回写发布者，也不能改变事件语义。

### 保留的用户请求事件

托盘和窗口层可以保留请求型事件：

- 显示/隐藏宠物；
- 打开设置；
- 打开角色包目录；
- 请求退出。

这些事件表达 UI 意图，不代表操作已经成功。实际结果由应用服务或 UI 命令负责反馈。

### 不再扩大的事件

- 不为每个按钮点击增加全局事件；
- 不让 ViewModel 之间互相订阅业务事件；
- 不用无参数 `StateChanged` 代替完整状态快照；
- 不用事件通知替代同步方法返回结果；
- 不让 `PropertyChanged` 承担跨模块状态传播。

## 六条关键流程

### 启动与恢复

```text
AppStartup
  -> RoleActivationService.RestoreAsync
  -> AppRuntime.RoleState / PortraitState
  -> AppLifecycleChanged
  -> DesktopAppUi.ShowPortrait
```

### 设置页激活角色

```text
RolePackageDetailViewModel
  -> RoleActivationService.ActivateAsync
  -> ActiveRoleChanged + PortraitChanged
  -> 面板、对话、窗口读取统一状态
```

### 开始专注

```text
AttachedPanelViewModel
  -> FocusApplicationService.Start
  -> FocusSessionService
  -> FocusChanged
  -> 面板更新计时器
  -> FocusFeedbackCoordinator 处理对白和表情
```

### 个性化缩放

```text
PersonalizationViewModel
  -> PersonalizationService.SaveScale
  -> AppRuntime.PortraitState / PortraitController
  -> PortraitChanged
  -> PortraitWindowCoordinator 更新布局
```

### 对话与 Todo

```text
ConversationViewModel
  -> ConversationApplicationService.Send
  -> ConversationSnapshot / MessageAdded
  -> TodoProposalResult
  -> 用户确认命令
  -> TodoApplicationService
```

### Agent 运行时

```text
Relay / Adapter
  -> AgentRuntimeSnapshot
  -> AgentRuntimeApplicationService
  -> AppRuntime.AgentState
  -> 连接设置、任务面板、托盘状态
```

## 分阶段迁移计划

### Phase 1：定义状态模型

- 增加 `ActiveRoleSnapshot`、`PortraitSnapshot` 和 `AppRuntime` 状态切片入口；
- 明确每个状态的唯一写入者；
- 保留现有事件作为兼容层；
- 不改变用户可见行为。

### Phase 2：统一角色激活

- 新增 `RoleActivationService`；
- 迁移启动恢复、设置页激活、最近角色切换；
- 移除 `ServantFocusConnector` 对角色身份的私有缓存；
- 增加激活失败和角色包缺失测试。

### Phase 3：收敛专注和立绘事件

- 将 `SnapshotChanged` 转为完整 `FocusSnapshot`；
- 将立绘状态统一投影到 `AppRuntime.PortraitState`；
- 拆分 `FocusFeedbackCoordinator`；
- 面板不再暴露业务性的 `SetActiveServant`。

### Phase 4：收敛对话、Todo 和 Agent

- 区分消息流、对话快照、Todo 提案结果和 Agent 派发结果；
- 用应用服务结果替代隐式事件链；
- 后续接入工具调用/MCP 时保持命令边界，不让模型输出直接驱动外部执行。

### Phase 5：加入可选能力模块

- 建立编译时模块注册入口；
- 增加 `Voice` 和 `Files` 模块的状态与适配器边界；
- 验证模块不可用时核心功能仍可启动；
- 暂不实现动态插件发现、下载和热加载。

### Phase 6：清理兼容层

- 删除重复身份缓存和无参数状态事件；
- 删除只为转发事件而存在的 ViewModel 方法；
- 为每条状态链补充端到端验收。

## 验收标准

架构迁移完成后，应满足：

- 任意角色激活入口都会产生相同的 `ActiveRoleSnapshot`；
- 面板、对话、反馈和窗口看到的是同一当前角色；
- 缩放、DPI、表情变化不会绕过肖像状态；
- 专注状态只有一个事实来源；
- 托盘请求不会被误认为操作成功；
- Agent 外部事件不会直接穿透到界面；
- 订阅者可以在测试中独立替换，不需要启动 WPF 窗口；
- 删除任何一个 ViewModel 后，不会破坏领域服务的核心状态机。

## 文档边界

本文描述目标架构和迁移顺序。具体实现决策、测试记录、发布验收和历史交接记录继续作为本地开发材料维护，不在本文中堆积。
