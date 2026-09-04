# 核心事件与状态迁移实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将角色、立绘、专注、对话、Agent 和窗口的运行时状态收敛到明确的模块边界，并统一角色激活路径。

**Architecture:** 采用 `AppRuntime` 组合状态切片；每个模块拥有自己的状态和服务。通过 `RoleActivationService` 统一角色激活，通过携带快照的类型化事件同步消费者；不引入全局 Event Bus，也不让 ViewModel 直接互相传递业务状态。

**Tech Stack:** C# / .NET 8 / WPF / CommunityToolkit.Mvvm / Microsoft.Extensions.DependencyInjection / xUnit。

**Spec:** `docs/internal/event-and-state-architecture.md`

## Global Constraints

- 领域服务不直接调用 ViewModel、WPF 控件或 Dispatcher。
- `INotifyPropertyChanged` 只用于本 ViewModel 的界面刷新，不作为模块间业务协议。
- 状态变化事件应携带新快照或足够的变化信息。
- 一次性用户请求使用命令或返回结果；不为每个按钮点击增加全局事件。
- 不引入全局 Event Bus；跨模块通信使用明确服务接口和类型化事件/快照。
- 新模块以编译时注册为第一阶段目标，不实现动态插件发现、下载和热加载。
- Voice 和 Files 是本计划之外的后续独立模块；本计划只建立可供它们接入的核心边界。
- 每个任务遵循 TDD：先写失败测试，再写最小实现，再运行全量相关测试。

---

## 文件与模块地图

### 第一阶段新增文件

- Create: `src/FgoPet.App/Runtime/AppRuntime.cs` — 组合模块状态，不执行业务动作。
- Create: `src/FgoPet.App/Runtime/RoleState.cs` — 当前角色快照。
- Create: `src/FgoPet.App/Runtime/PortraitRuntimeState.cs` — 当前立绘快照投影。
- Create: `src/FgoPet.App/Runtime/AppStateChangedEventArgs.cs` — 携带新快照的应用状态事件。
- Create: `src/FgoPet.App/Servants/RoleActivationService.cs` — 统一角色激活用例。
- Create: `tests/FgoPet.App.Tests/Runtime/AppRuntimeTests.cs` — 状态切片和事件测试。
- Create: `tests/FgoPet.App.Tests/Servants/RoleActivationServiceTests.cs` — 激活成功/失败测试。

### 需要修改的现有文件

- Modify: `src/FgoPet.App/Servants/ServantFocusConnector.cs` — 移除角色身份私有缓存，收敛为反馈协调器。
- Modify: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs` — 激活入口委托给角色激活服务。
- Modify: `src/FgoPet.App/Settings/RolePackageDetailViewModel.cs` — 使用统一角色激活服务。
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppShell.cs` — 启动恢复使用统一激活服务。
- Modify: `src/FgoPet.App/Panels/AttachedPanelViewModel.cs` — 从运行时状态读取当前角色和专注状态。
- Modify: `src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs` — 消费立绘快照，不重新解释底层状态。
- Modify: `src/FgoPet.App/Settings/PersonalizationViewModel.cs` — 通过立绘服务应用缩放；保持现有回归测试。
- Modify: `src/FgoPet.App/Dialogue/ConversationViewModel.cs` — 通过运行时角色状态初始化对话上下文。
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs` — 注册运行时状态和应用服务。

### 后续独立计划

- `docs/internal/voice-module-plan.md` — 语音状态、播放服务和 Windows 音频适配器。
- `docs/internal/file-module-plan.md` — 文件操作服务、文件状态和文件系统适配器。

---

## Task 1: 建立运行时状态切片

**Files:**

- Create: `src/FgoPet.App/Runtime/RoleState.cs`
- Create: `src/FgoPet.App/Runtime/PortraitRuntimeState.cs`
- Create: `src/FgoPet.App/Runtime/AppRuntime.cs`
- Create: `src/FgoPet.App/Runtime/AppStateChangedEventArgs.cs`
- Test: `tests/FgoPet.App.Tests/Runtime/AppRuntimeTests.cs`

**Interfaces:**

- Consumes: `FgoPet.Core.Portraits.PortraitSelection` and existing `PortraitState` data.
- Produces: `AppRuntime.ActiveRole`, `AppRuntime.Portrait`, `AppRuntime.SetActiveRole(...)`, `AppRuntime.SetPortrait(...)`, `AppRuntime.ActiveRoleChanged`, `AppRuntime.PortraitChanged`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Setting_active_role_publishes_the_new_snapshot()
{
    var runtime = new AppRuntime();
    ActiveRoleState? published = null;
    runtime.ActiveRoleChanged += (_, args) => published = args.State;

    var state = new ActiveRoleState("pack", "casual", "1.0.0", "mash_kyrielight");
    runtime.SetActiveRole(state);

    Assert.Equal(state, runtime.ActiveRole);
    Assert.Equal(state, published);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~AppRuntimeTests --no-restore`

Expected: FAIL because `AppRuntime`, `ActiveRoleState`, and `AppStateChangedEventArgs<T>` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record ActiveRoleState(
    string PackageId,
    string AppearanceId,
    string PackageVersion,
    string ServantId);

public sealed class AppStateChangedEventArgs<T>(T state) : EventArgs
{
    public T State { get; } = state;
}

public sealed class AppRuntime
{
    public ActiveRoleState? ActiveRole { get; private set; }
    public event EventHandler<AppStateChangedEventArgs<ActiveRoleState>>? ActiveRoleChanged;

    public void SetActiveRole(ActiveRoleState state)
    {
        ActiveRole = state ?? throw new ArgumentNullException(nameof(state));
        ActiveRoleChanged?.Invoke(this, new(state));
    }
}
```

将 `PortraitRuntimeState` 使用同样的快照事件模式实现，字段至少包含 `IsActivated`、`Scale`、`Expression` 和 `Geometry`；不要在状态类中放置激活或缩放方法。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~AppRuntimeTests --no-restore`

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.App/Runtime tests/FgoPet.App.Tests/Runtime
git commit -m "refactor: add runtime state slices"
```

## Task 2: 抽出统一角色激活服务

**Files:**

- Create: `src/FgoPet.App/Servants/RoleActivationService.cs`
- Create: `tests/FgoPet.App.Tests/Servants/RoleActivationServiceTests.cs`
- Modify: `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`

**Interfaces:**

- Consumes: `IArtPackageRepository`, `IPortraitController`, `IAppSettingsStore`, `AppRuntime`.
- Produces: `Task<RoleActivationResult> ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken)` and `Task<RoleActivationResult> RestoreAsync(CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing tests**

覆盖三个行为：

```csharp
[Fact]
public async Task ActivateAsync_updates_role_state_after_portrait_activation()
{
    var service = CreateService();
    var result = await service.ActivateAsync(Selection, CancellationToken.None);

    Assert.True(result.Succeeded);
    Assert.Equal("mash_kyrielight", Runtime.ActiveRole!.ServantId);
    Assert.Equal(Selection, Settings.Load().Selection);
}

[Fact]
public async Task ActivateAsync_does_not_update_settings_when_portrait_activation_fails()
{
    var service = CreateFailingService();
    var result = await service.ActivateAsync(Selection, CancellationToken.None);

    Assert.False(result.Succeeded);
    Assert.Null(Runtime.ActiveRole);
    Assert.Null(Settings.Load().Selection);
}

[Fact]
public async Task RestoreAsync_returns_no_selection_when_saved_package_is_missing()
{
    var service = CreateServiceWithMissingSelection();

    var result = await service.RestoreAsync(CancellationToken.None);

    Assert.False(result.Succeeded);
    Assert.Equal(RoleActivationFailure.MissingPackage, result.Failure);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~RoleActivationServiceTests --no-restore`

Expected: FAIL because the service and result types do not exist.

- [ ] **Step 3: Write minimal implementation**

实现 `RoleActivationService` 时遵循以下顺序：

1. 调用 repository 解析 `PortraitSelection` 对应的 `AppearanceLocation` 和 `ServantId`。
2. 调用 `IPortraitController.ActivateAsync`。
3. 成功后创建 `ActiveRoleState` 并写入 `AppRuntime`。
4. 成功后保存 `IAppSettingsStore.Selection`。
5. 失败时返回 `RoleActivationResult.Failed(...)`，不写入运行时状态和持久化选择。

`RoleActivationResult` 至少包含 `Succeeded`、`ActiveRole` 和 `Failure`；不要让 UI 解析 `PackFailureException` 来决定状态。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~RoleActivationServiceTests --no-restore`

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/FgoPet.App/Servants/RoleActivationService.cs tests/FgoPet.App.Tests/Servants/RoleActivationServiceTests.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs
git commit -m "refactor: centralize role activation"
```

## Task 3: 迁移全部角色激活入口

**Files:**

- Modify: `src/FgoPet.App/Bootstrap/DesktopAppShell.cs`
- Modify: `src/FgoPet.App/Servants/ServantLibraryViewModel.cs`
- Modify: `src/FgoPet.App/Settings/RolePackageDetailViewModel.cs`
- Modify: `src/FgoPet.App/Panels/RecentServantSwitcherViewModel.cs`
- Test: 对应现有 ViewModel 测试文件；新增失败测试到 `tests/FgoPet.App.Tests/Servants/RoleActivationEntryPointTests.cs`

**Interfaces:**

- Consumes: `RoleActivationService.ActivateAsync` and `RestoreAsync`.
- Produces: All four entry points use the same service and publish the same `ActiveRoleState`.

- [ ] **Step 1: Write the failing test**

为启动恢复、设置页激活和最近角色切换分别记录 fake `RoleActivationService` 的调用，并断言三者传入相同的 `PortraitSelection`。另外断言设置页激活失败时不会自行保存选择。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~RoleActivationEntryPointTests --no-restore`

Expected: FAIL because the entry points still call `IPortraitController` or `ServantFocusConnector` directly.

- [ ] **Step 3: Write minimal implementation**

删除以下直接调用：

```csharp
await _controller.ActivateAsync(selection, CancellationToken.None);
```

将其替换为：

```csharp
var result = await _activation.ActivateAsync(selection, CancellationToken.None);
if (!result.Succeeded)
{
    Diagnostic = result.ToDiagnostic();
    return;
}
```

`DesktopAppShell` 只负责调用 `RestoreAsync` 并根据结果显示角色包列表或宠物窗口；不再自行解析 `servant_id`。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~RoleActivationEntryPointTests --no-restore`

Expected: PASS。

- [ ] **Step 5: Run the related full suite**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --no-restore`

Expected: Existing App tests remain green。

- [ ] **Step 6: Commit**

```bash
git add src/FgoPet.App/Bootstrap/DesktopAppShell.cs src/FgoPet.App/Servants/ServantLibraryViewModel.cs src/FgoPet.App/Settings/RolePackageDetailViewModel.cs src/FgoPet.App/Panels/RecentServantSwitcherViewModel.cs tests/FgoPet.App.Tests
git commit -m "refactor: route role activation through application service"
```

## Task 4: 收敛专注状态与反馈协调

**Files:**

- Create: `src/FgoPet.App/Focus/FocusSnapshot.cs`
- Create: `src/FgoPet.App/Focus/FocusFeedbackCoordinator.cs`
- Modify: `src/FgoPet.App/Focus/FocusSessionService.cs`
- Modify: `src/FgoPet.App/Servants/ServantFocusConnector.cs`
- Modify: `src/FgoPet.App/Panels/AttachedPanelViewModel.cs`
- Test: `tests/FgoPet.App.Tests/Panels/Phase2AttachedPanelViewModelTests.cs` and new `tests/FgoPet.App.Tests/Focus/FocusFeedbackCoordinatorTests.cs`

**Interfaces:**

- Consumes: `ActiveRoleState`, focus commands and existing feedback selector.
- Produces: `FocusSnapshotChanged(FocusSnapshot snapshot)` and `FocusFeedbackCoordinator.Handle(FocusSnapshot snapshot, ActiveRoleState role)`.

- [ ] **Step 1: Write the failing tests**

覆盖：

```csharp
[Fact]
public void Starting_focus_requires_active_role_state()
{
    var panel = CreatePanelWithFocusState(FocusStatus.Idle);

    Assert.False(panel.CanStartFocus);
}

[Fact]
public void Feedback_coordinator_uses_role_from_runtime_state()
{
    var coordinator = CreateCoordinator();

    coordinator.Handle(FocusingSnapshot, new ActiveRoleState("pack", "casual", "1.0.0", "mash_kyrielight"));

    Assert.Equal("mash_kyrielight", PublishedEvent.ServantId);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~FocusFeedbackCoordinatorTests --no-restore`

Expected: FAIL because `FocusSnapshot` and `FocusFeedbackCoordinator` do not exist.

- [ ] **Step 3: Write minimal implementation**

将 `FocusSessionService` 当前无参数 `SnapshotChanged` 事件改为发布完整 `FocusSnapshot`。将 `ServantFocusConnector` 中的 `_activeServantId` 删除，反馈处理器从 `AppRuntime.ActiveRole` 获取身份；如果角色状态为空，则只记录中性反馈，不启动外部调用。

`AttachedPanelViewModel` 的 `CanStartFocus` 由 `AppRuntime.ActiveRole?.ServantId` 是否存在决定，不新增角色包库事件。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~FocusFeedbackCoordinatorTests --no-restore`

Expected: PASS。

- [ ] **Step 5: Run the related full suite**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --no-restore`

Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add src/FgoPet.App/Focus src/FgoPet.App/Servants/ServantFocusConnector.cs src/FgoPet.App/Panels/AttachedPanelViewModel.cs tests/FgoPet.App.Tests
git commit -m "refactor: centralize focus snapshots and feedback"
```

## Task 5: 统一立绘状态、缩放和窗口刷新

**Files:**

- Modify: `src/FgoPet.App/Portraits/PortraitController.cs`
- Modify: `src/FgoPet.App/Settings/PersonalizationViewModel.cs`
- Modify: `src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppUi.cs`
- Test: `tests/FgoPet.App.Tests/Settings/PersonalizationViewModelTests.cs` and relevant portrait/window tests

**Interfaces:**

- Consumes: `AppRuntime.Portrait`, `IPortraitController` and `PortraitSnapshot`.
- Produces: One portrait state publication path for activation, scale, expression and DPI.

- [ ] **Step 1: Write the failing test**

增加两个回归测试：

```csharp
[Fact]
public void Scale_change_publishes_a_portrait_snapshot()
{
    var controller = CreateActivatedController();
    PortraitState? state = null;
    controller.StateChanged += (_, _) => state = controller.CurrentState;

    controller.SetScale(0.75);

    Assert.Equal(0.75, state!.Scale);
}

[Fact]
public void Settings_scale_change_is_ignored_by_controller_until_activation()
{
    var portrait = new FakePortraitControllerWithoutActivation();
    var viewModel = new PersonalizationViewModel(new FakeSettingsStore(), portrait);

    viewModel.Scale = 0.75;

    Assert.Equal(0.75, viewModel.Scale);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~PersonalizationViewModelTests --no-restore`

Expected: The new snapshot/wiring assertions fail before the state projection is unified.

- [ ] **Step 3: Write minimal implementation**

保持当前缩放设置修复，但将事件投影统一到 `AppRuntime.Portrait`；`PortraitWindowCoordinator` 只消费快照，不重新计算当前缩放来源。`DesktopAppUi` 只处理显示/隐藏和恢复，不读取立绘业务字段。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~PersonalizationViewModelTests --no-restore`

Expected: PASS。

- [ ] **Step 5: Run the related full suite**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --no-restore`

Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add src/FgoPet.App/Portraits src/FgoPet.App/Settings/PersonalizationViewModel.cs src/FgoPet.App/Windowing src/FgoPet.App/Bootstrap/DesktopAppUi.cs tests/FgoPet.App.Tests
git commit -m "refactor: publish unified portrait state"
```

## Task 6: 收敛对话、Todo 和 Agent 状态边界

**Files:**

- Modify: `src/FgoPet.App/Dialogue/ConversationOrchestrator.cs`
- Modify: `src/FgoPet.App/Dialogue/ConversationViewModel.cs`
- Modify: `src/FgoPet.App/ViewModels/AgentCurrentTaskViewModel.cs`
- Modify: `src/FgoPet.App/Services/AgentReconciliationService.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppShell.cs`
- Test: existing dialogue and Agent test projects; add `tests/FgoPet.App.Tests/Dialogue/ConversationStateTests.cs`

**Interfaces:**

- Consumes: `AppRuntime.ActiveRole`, provider responses, Relay snapshots and Todo application results.
- Produces: `ConversationSnapshot`, `AgentRuntimeSnapshot`, `TodoProposalResult`; no direct ViewModel-to-ViewModel business events.

- [ ] **Step 1: Write the failing tests**

覆盖：

- 切换 `ActiveRole` 后对话上下文只使用新的 `ServantId`；
- 模型返回 Todo 提案时先返回提案结果，不直接创建 Todo；
- Agent Relay 快照变化不会直接调用窗口或面板方法；
- 外部连接失败只更新 Agent 状态，不阻塞角色和专注状态。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~ConversationStateTests --no-restore`

Expected: FAIL because the new snapshot boundary is not present.

- [ ] **Step 3: Write minimal implementation**

保留当前 Todo 安全规则：提案字段白名单、用户确认后创建、禁止模型直接执行外部工具。将 `ConversationOrchestrator.Updated` 和 Agent 运行时快照包装为所属模块的状态更新；ViewModel 只订阅模块状态并生成 `PropertyChanged`。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter FullyQualifiedName~ConversationStateTests --no-restore`

Expected: PASS。

- [ ] **Step 5: Run all tests**

Run: `dotnet test FgoPet.sln --no-restore`

Expected: PASS with no failed test projects。

- [ ] **Step 6: Commit**

```bash
git add src/FgoPet.App/Dialogue src/FgoPet.App/ViewModels src/FgoPet.App/Services src/FgoPet.App/Bootstrap/DesktopAppShell.cs tests
git commit -m "refactor: isolate conversation and agent state"
```

## Task 7: 生命周期、托盘和兼容层清理

**Files:**

- Modify: `src/FgoPet.App/Tray/TrayService.cs`
- Modify: `src/FgoPet.App/Bootstrap/DesktopAppUi.cs`
- Modify: `src/FgoPet.App/Bootstrap/AppStartup.cs`
- Modify: `src/FgoPet.App/Main/PortraitWindow.xaml.cs`
- Modify: `src/FgoPet.App/Windowing/PortraitWindowCoordinator.cs`
- Test: `tests/FgoPet.App.Tests` and `tests/FgoPet.EndToEnd.Tests`
- Update: `docs/internal/event-and-state-architecture.md`

**Interfaces:**

- Consumes: `AppLifecycleState`, `PortraitSnapshot` and UI request events.
- Produces: Explicit separation between UI requests and successful application state changes.

- [ ] **Step 1: Write the failing tests**

覆盖：

- 托盘“显示/隐藏”只发出 UI 请求，不伪造角色激活成功；
- 角色恢复失败时进入角色包设置页并保留结构化错误；
- 正常退出解除所有服务订阅；
- `PortraitWindowCoordinator` 和面板不会重复订阅同一底层事件。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FgoPet.EndToEnd.Tests/FgoPet.EndToEnd.Tests.csproj --no-restore`

Expected: 至少一个新生命周期断言失败，证明测试覆盖了旧的混合边界。

- [ ] **Step 3: Write minimal implementation**

保留 `TrayService` 的请求型事件；将成功/失败结果留在 `DesktopAppUi` 和应用服务中。删除已经被状态服务取代的转发方法和重复订阅，但不删除仍被 WPF 绑定使用的 `PropertyChanged`。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test FgoPet.sln --no-restore`

Expected: 所有测试项目通过。

- [ ] **Step 5: Run build verification**

Run: `dotnet build FgoPet.sln --no-restore`

Expected: 0 warnings, 0 errors。

- [ ] **Step 6: Update architecture status**

在 `docs/internal/event-and-state-architecture.md` 增加“已迁移/待迁移”小节，明确 Voice 和 Files 尚未实现，并列出剩余兼容层。

- [ ] **Step 7: Commit**

```bash
git add src/FgoPet.App/Tray src/FgoPet.App/Bootstrap src/FgoPet.App/Main src/FgoPet.App/Windowing tests docs/internal/event-and-state-architecture.md
git commit -m "refactor: complete event state migration boundaries"
```

## 迁移完成后的独立后续计划

核心迁移完成并通过全量验证后，再分别创建并执行：

1. `voice-module-plan.md`：语音状态、合成/播放服务、Windows 音频适配器、设备不可用降级。
2. `file-module-plan.md`：文件操作命令、文件状态、文件系统适配器、权限和清理策略。
3. `module-registration-plan.md`：编译时可选模块注册和启动/停止生命周期；只有需求证明必要时才评估动态插件系统。

每个后续计划都必须保持模块独立可测试，并通过 `AppRuntime` 或明确的应用服务接口接入，不直接向现有 ViewModel 注入跨模块事件。
