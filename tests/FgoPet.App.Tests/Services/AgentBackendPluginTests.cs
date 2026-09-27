using FgoPet.AgentBackend;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using FgoPet.Extensibility;
using FgoPet.Kernel.Lifecycle;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class AgentBackendPluginTests
{
    [Fact]
    public async Task Disabled_startup_is_observed_once_and_stop_cancels_pending_work()
    {
        var runtime = new PendingRuntime();
        var tracker = new BackgroundOperationTracker(new ProcessLifetime());
        var reconnect = new AgentReconnectService(new ReconnectGateway(false), new ReconnectAgents(Execution()), new());
        await using var plugin = new AgentBackendPlugin(runtime, reconnect, () => false, tracker);
        await plugin.StartAsync(default);
        await plugin.OnApplicationReadyAsync(new(true), default);
        await plugin.OnApplicationReadyAsync(new(true), default);
        Assert.Equal(1, runtime.Calls);
        Assert.False(runtime.Enabled);
        await plugin.StopAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(tracker.Failures);
        Assert.Empty(plugin.Contributions.Workspaces);
        Assert.Empty(plugin.Contributions.Tools);
    }

    [Fact]
    public async Task Online_cycle_reconnects_persisted_state_and_detaches_on_stop()
    {
        var runtime = new ReadyRuntime();
        var gateway = new ReconnectGateway(false);
        var tracker = new BackgroundOperationTracker(new ProcessLifetime());
        var reconnect = new AgentReconnectService(gateway, new ReconnectAgents(Execution()), new());
        await using var plugin = new AgentBackendPlugin(runtime, reconnect, () => true, tracker);
        await plugin.StartAsync(default);
        await plugin.OnApplicationReadyAsync(new(true), default);
        Assert.Equal(1, runtime.Calls);
        gateway.Connected = true;
        runtime.PublishOnline();
        await gateway.Queried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.PublishOnline();
        Assert.Equal(1, gateway.QueryCount);
        await plugin.StopAsync(default);
        runtime.PublishOffline();
        runtime.PublishOnline();
        Assert.Equal(1, gateway.QueryCount);
    }

    [Fact]
    public async Task Enabling_after_disabled_start_reconnects_once_per_online_cycle()
    {
        var runtime = new ReadyRuntime();
        var gateway = new ReconnectGateway(false);
        var tracker = new BackgroundOperationTracker(new ProcessLifetime());
        var reconnect = new AgentReconnectService(gateway, new ReconnectAgents(Execution()), new());
        var enabled = false;
        await using var plugin = new AgentBackendPlugin(runtime, reconnect, () => enabled, tracker);
        await plugin.StartAsync(default);
        await plugin.OnApplicationReadyAsync(new(true), default);
        Assert.Equal(0, gateway.QueryCount);
        enabled = true;
        gateway.Connected = true;
        runtime.PublishOnline();
        await gateway.Queried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.PublishOnline();
        Assert.Equal(1, gateway.QueryCount);
        enabled = false;
        runtime.PublishOffline();
        enabled = true;
        runtime.PublishOnline();
        await gateway.SecondQueried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, gateway.QueryCount);
    }

    private static AgentExecution Execution() => new("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", DateTimeOffset.UtcNow);
    private sealed class PendingRuntime : IAgentRelayRuntime
    {
        private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Enabled { get; private set; }
        public AgentRelaySnapshot Current => AgentRelaySnapshot.Disabled;
        public event Action<AgentRelaySnapshot>? SnapshotChanged { add { } remove { } }
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        { Calls++; Enabled = enabled; return _pending.Task.WaitAsync(cancellationToken); }
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

}
