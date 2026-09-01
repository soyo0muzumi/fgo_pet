using FgoPet.App.Bootstrap;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class DesktopAppShellTests
{
    [Fact]
    public async Task Disabled_startup_notifies_runtime_once_without_waiting_for_it()
    {
        var runtime = new PendingRuntime();
        var ui = new FakeUi();
        var shell = new DesktopAppShell(new FakeRepository(null), new FakeController(), new FakeSettings(), ui, agentRuntime: runtime);
        await shell.StartAsync([], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await shell.StartAsync([], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(ui.LibraryShown);
        Assert.Equal(1, runtime.Calls);
        Assert.False(runtime.Enabled);
        runtime.Complete();
    }

    [Fact]
    public async Task Enabled_runtime_startup_reconnects_persisted_agent_state()
    {
        var runtime = new ReadyRuntime();
        var execution = new AgentExecution(
            "execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", DateTimeOffset.UtcNow);
        var gateway = new ReconnectGateway(connected: false);
        var agents = new ReconnectAgents(execution);
        var reconnect = new AgentReconnectService(gateway, agents, new AgentEventProjector());
        var shell = new DesktopAppShell(
            new FakeRepository(null),
            new FakeController(),
            new FakeSettings(agentEnabled: true),
            new FakeUi(),
            agentReconnect: reconnect,
            agentRuntime: runtime);

        await shell.StartAsync([], CancellationToken.None);

        Assert.Equal(1, runtime.Calls);
        gateway.Connected = true;
        runtime.PublishOnline();
        await gateway.Queried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gateway.QueryCount >= 1);
    }

    [Fact]
    public async Task Runtime_enable_after_disabled_start_reconnects_once_per_online_cycle()
    {
        var runtime = new ReadyRuntime();
        var execution = new AgentExecution(
            "execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", DateTimeOffset.UtcNow);
        var gateway = new ReconnectGateway(connected: false);
        var agents = new ReconnectAgents(execution);
        var reconnect = new AgentReconnectService(gateway, agents, new AgentEventProjector());
        var settings = new FakeSettings(agentEnabled: false);
        var shell = new DesktopAppShell(
            new FakeRepository(null),
            new FakeController(),
            settings,
            new FakeUi(),
            agentReconnect: reconnect,
            agentRuntime: runtime);

        await shell.StartAsync([], CancellationToken.None);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(0, gateway.QueryCount);

        settings.AgentEnabled = true;
        gateway.Connected = true;
        runtime.PublishOnline();
        await gateway.Queried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, gateway.QueryCount);

        // Repeated online snapshots in the same cycle do not replay recovery.
        runtime.PublishOnline();
        await Task.Delay(50);
        Assert.Equal(1, gateway.QueryCount);

        // Disabling starts a new cycle, so the next enabled online snapshot
        // gets exactly one recovery attempt again.
        settings.AgentEnabled = false;
        runtime.PublishOffline();
        settings.AgentEnabled = true;
        runtime.PublishOnline();
        await gateway.SecondQueried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, gateway.QueryCount);
    }

    private sealed class PendingRuntime : IAgentRelayRuntime
    {
        private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Enabled { get; private set; }
        public AgentRelaySnapshot Current => AgentRelaySnapshot.Disabled;
        public event Action<AgentRelaySnapshot>? SnapshotChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        { Calls++; Enabled = enabled; return _pending.Task; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Complete() => _pending.TrySetResult();
    }

    private sealed class ReadyRuntime : IAgentRelayRuntime
    {
        public int Calls { get; private set; }
        public AgentRelaySnapshot Current => AgentRelaySnapshot.Disabled;
        public event Action<AgentRelaySnapshot>? SnapshotChanged;
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PublishOnline() => SnapshotChanged?.Invoke(new AgentRelaySnapshot(
            AgentRelayConnectionState.Connected,
            true,
            true,
            true,
            DateTimeOffset.UtcNow,
            [],
            []));
        public void PublishOffline() => SnapshotChanged?.Invoke(AgentRelaySnapshot.Disabled);
    }

    private sealed class ReconnectGateway(bool connected) : IAgentGateway
    {
        public int QueryCount { get; private set; }
        public bool Connected { get; set; } = connected;
        public TaskCompletionSource Queried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondQueried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => Connected;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentGatewayStatus(Connected, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(
            IReadOnlyList<AgentExecution> knownExecutions,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            Queried.TrySetResult();
            if (QueryCount == 2)
            {
                SecondQueried.TrySetResult();
            }
            return Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        }
    }

    private sealed class ReconnectAgents(AgentExecution execution) : IAgentRepository
    {
        public void SaveExecution(AgentExecution value) { }
        public AgentExecution? GetExecution(string id) => execution.Id == id ? execution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => execution;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => new[] { execution };
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => Array.Empty<AgentExecution>();
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => Array.Empty<AgentArchiveBatch>();
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }

    [Fact]
    public async Task Start_without_installed_pack_keeps_tray_and_shows_library()
    {
        var ui = new FakeUi();
        var shell = new DesktopAppShell(new FakeRepository(null), new FakeController(), new FakeSettings(), ui);

        await shell.StartAsync([], CancellationToken.None);

        Assert.True(ui.TrayInitialized);
        Assert.True(ui.LibraryShown);
        Assert.False(ui.PortraitShown);
    }

    [Fact]
    public async Task Start_with_valid_selection_activates_and_shows_portrait()
    {
        var selection = new PortraitSelection("official.mash", "casual", "1.0.0");
        var controller = new FakeController();
        var shell = new DesktopAppShell(new FakeRepository(new AppearanceLocation(new PackIdentity("official.mash", "1.0.0"), "casual", "C:\\pack")), controller, new FakeSettings(selection), new FakeUi());

        await shell.StartAsync([], CancellationToken.None);

        Assert.Equal(selection, controller.Activated);
    }

    [Fact]
    public async Task Start_with_pack_argument_opens_it_in_library_without_auto_activation()
    {
        var ui = new FakeUi();
        var shell = new DesktopAppShell(new FakeRepository(null), new FakeController(), new FakeSettings(), ui);

        await shell.StartAsync(["C:\\Downloads\\mash.fgopetpack"], CancellationToken.None);

        Assert.Equal("C:\\Downloads\\mash.fgopetpack", ui.OfferedPack);
        Assert.True(ui.LibraryShown);
    }

    private sealed class FakeUi : IDesktopAppUi
    {
        public bool TrayInitialized { get; private set; }
        public bool LibraryShown { get; private set; }
        public bool PortraitShown { get; private set; }
        public string? OfferedPack { get; private set; }
        public void InitializeTray() => TrayInitialized = true;
        public void ShowLibrary(string? offeredPackPath = null) { LibraryShown = true; OfferedPack = offeredPackPath; }
        public void ShowPortrait() => PortraitShown = true;
    }

    private sealed class FakeSettings(PortraitSelection? selection = null, bool agentEnabled = false) : IAppSettingsStore
    {
        public bool AgentEnabled { get; set; } = agentEnabled;
        public string Location => "settings.json";
        public AppSettings Load() => AppSettings.Defaults with
        {
            Selection = selection,
            AgentConnection = new AgentConnectionSettings(AgentEnabled),
        };
        public void Save(AppSettings settings) { }
    }

    private sealed class FakeController : IPortraitController
    {
        public PortraitSelection? Activated { get; private set; }
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) { Activated = selection; return Task.CompletedTask; }
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Core.Geometry.Dpi2 dpi) { }
    }

    private sealed class FakeRepository(AppearanceLocation? resolved) : IArtPackageRepository
    {
        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(new PackCatalog([]));
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<InstalledServant>>([]);
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.FromResult(resolved);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) => Task.FromResult(resolved);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
