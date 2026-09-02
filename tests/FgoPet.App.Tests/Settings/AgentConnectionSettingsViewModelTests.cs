using FgoPet.App.Settings;
using FgoPet.App.ViewModels;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class AgentConnectionSettingsViewModelTests
{
    [Fact]
    public async Task Saves_global_and_source_switches_without_touching_allowlist()
    {
        var settings = new FakeSettingsStore();
        var repository = new FakeAgentRepository();
        repository.Connections.Add(new PersistedAgentConnection(
            "codex", "Codex", "1", true, null, 0,
            new AgentCapabilities(true, true, OpenMode.AppOnly, new[] { new AgentProjectTarget("target-1", "Project") })));
        var viewModel = new AgentConnectionSettingsViewModel(settings, repository);

        viewModel.Enabled = true;
        viewModel.Connections.Single().IsEnabled = false;
        await viewModel.SaveAsync();

        Assert.True(settings.Current.AgentConnection.Enabled);
        Assert.False(settings.Current.AgentConnection.SourceEnabled["codex"]);
        Assert.Equal("target-1", settings.Current.AgentConnection.ProjectAllowlist["codex"].Single().TargetId);
    }

    [Fact]
    public async Task Administration_snapshot_exposes_pending_and_instance_scoped_permissions()
    {
        var settings = new FakeSettingsStore();
        var repository = new FakeAgentRepository();
        var snapshot = Snapshot(
            new AgentPendingSource("request-1", "codex", "instance-1", "Codex", "1.2", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(9)),
            new AgentApprovedSource("codex", "instance-2", "Codex (work)", "1.2", true, new[] { "project-a", "project-b" }, true));
        var administration = new FakeAdministration(snapshot);
        using var viewModel = new AgentConnectionSettingsViewModel(settings, repository, administration: administration);

        await viewModel.RefreshAsync();

        Assert.Single(viewModel.PendingSources);
        Assert.Equal("instance-1", viewModel.PendingSources.Single().SourceInstanceId);
        Assert.Single(viewModel.ApprovedSources);
        var approved = viewModel.ApprovedSources.Single();
        approved.ApplyCatalog(new[]
        {
            new AgentTargetDescriptor("project-a", "Project A", false),
            new AgentTargetDescriptor("project-b", "Project B", false),
        });
        Assert.Equal(new[] { "Project A", "Project B" }, approved.Targets.Select(target => target.DisplayName));
        Assert.All(approved.Targets, target => Assert.True(target.IsSelected));
        Assert.Equal("已连接", viewModel.StateText);
    }

    [Fact]
    public async Task Administration_actions_use_source_instance_and_never_call_legacy_gateway_permissions()
    {
        var settings = new FakeSettingsStore();
        var repository = new FakeAgentRepository();
        var gateway = new FakeGateway();
        var source = new AgentApprovedSource("codex", "instance-2", "Codex", "1.2", true, new[] { "project-a" }, true);
        var administration = new FakeAdministration(Snapshot(approved: source));
        using var viewModel = new AgentConnectionSettingsViewModel(settings, repository, gateway: gateway, administration: administration);

        await viewModel.DecideRegistrationAsync("request-1", approve: true);
        var approved = viewModel.ApprovedSources.Single();
        approved.ApplyCatalog(new[]
        {
            new AgentTargetDescriptor("project-a", "Project A", false),
            new AgentTargetDescriptor("project-z", "Project Z", false),
            new AgentTargetDescriptor("project-y", "Project Y", false),
        });
        Assert.All(approved.Targets, target => target.IsSelected = false);
        approved.Targets.Single(target => target.TargetId == "project-z").IsSelected = true;
        approved.Targets.Single(target => target.TargetId == "project-y").IsSelected = true;
        approved.IsEnabled = false;
        await viewModel.SaveSourceAsync(approved);
        await viewModel.RevokeSourceAsync(approved);

        Assert.Equal(("request-1", true), Assert.Single(administration.Decisions));
        var update = Assert.Single(administration.PermissionUpdates);
        Assert.Equal("instance-2", update.SourceInstanceId);
        Assert.Equal(new[] { "project-z", "project-y" }, update.TargetIds);
        Assert.False(update.Enabled);
        Assert.Equal(("codex", "instance-2"), Assert.Single(administration.Revocations));
        Assert.Empty(gateway.SourceEnabledCalls);
        Assert.Empty(gateway.AllowedTargetCalls);
    }

    [Fact]
    public async Task Administration_global_save_updates_runtime_without_overwriting_legacy_source_maps()
    {
        var sourceEnabled = new Dictionary<string, bool> { ["legacy"] = true };
        var allowlist = new Dictionary<string, IReadOnlyList<AgentProjectTarget>>
        {
            ["legacy"] = new[] { new AgentProjectTarget("legacy-target", "Legacy") },
        };
        var settings = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                AgentConnection = new AgentConnectionSettings(false, sourceEnabled, allowlist),
            },
        };
        var runtime = new FakeRuntime(AgentRelaySnapshot.Disabled);
        using var viewModel = new AgentConnectionSettingsViewModel(
            settings,
            new FakeAgentRepository(),
            gateway: new FakeGateway(),
            administration: new FakeAdministration(AgentRelaySnapshot.Disabled),
            runtime: runtime);

        viewModel.Enabled = true;
        await viewModel.SaveAsync();

        Assert.True(settings.Current.AgentConnection.Enabled);
        Assert.True(settings.Current.AgentConnection.SourceEnabled["legacy"]);
        Assert.Equal("legacy-target", settings.Current.AgentConnection.ProjectAllowlist["legacy"].Single().TargetId);
        Assert.Equal(new[] { true }, runtime.EnabledCalls);
    }

    [Fact]
    public void Runtime_snapshot_subscription_is_removed_when_view_model_is_disposed()
    {
        var runtime = new FakeRuntime(AgentRelaySnapshot.Disabled);
        var viewModel = new AgentConnectionSettingsViewModel(runtime: runtime, settings: new FakeSettingsStore(), agents: new FakeAgentRepository());
        viewModel.Dispose();

        runtime.Publish(Snapshot());

        Assert.Equal(AgentRelayConnectionState.Disabled, viewModel.CurrentSnapshot.State);
    }

    [Fact]
    public void Runtime_offline_snapshot_preserves_unsaved_instance_permissions_and_editor_identity()
    {
        var source = new AgentApprovedSource("codex", "instance-1", "Codex", "1.2", true, new[] { "server-target" }, true);
        var runtime = new FakeRuntime(Snapshot(approved: source));
        using var viewModel = new AgentConnectionSettingsViewModel(
            runtime: runtime,
            settings: new FakeSettingsStore(),
            agents: new FakeAgentRepository(),
            administration: new FakeAdministration(Snapshot(approved: source)));

        var editor = Assert.Single(viewModel.ApprovedSources);
        editor.ApplyCatalog(new[]
        {
            new AgentTargetDescriptor("server-target", "Server project", false),
            new AgentTargetDescriptor("unsaved-target", "Unsaved project", false),
        });
        editor.Targets.Single(target => target.TargetId == "server-target").IsSelected = false;
        editor.Targets.Single(target => target.TargetId == "unsaved-target").IsSelected = true;
        runtime.Publish(new AgentRelaySnapshot(
            AgentRelayConnectionState.RelayOffline,
            RelayOnline: false,
            AppOnline: false,
            AdapterOnline: false,
            DateTimeOffset.UtcNow,
            Array.Empty<AgentPendingSource>(),
            Array.Empty<AgentApprovedSource>(),
            "relay_offline"));

        Assert.Same(editor, Assert.Single(viewModel.ApprovedSources));
        Assert.Equal("Unsaved project", Assert.Single(editor.Targets.Where(target => target.IsSelected)).DisplayName);
        Assert.Equal(new[] { "unsaved-target" }, editor.AllowedTargetIds);
    }

    [Fact]
    public void Applying_catalog_maps_saved_ids_to_names_and_preserves_unknown_ids()
    {
        var editor = new AgentApprovedSourceViewModel(
            new AgentApprovedSource("codex", "instance-1", "Codex", "1", true,
                new[] { "known-id", "missing-id" }, true));

        editor.ApplyCatalog(new[] { new AgentTargetDescriptor("known-id", "Project A", false) });

        Assert.Equal("Project A", Assert.Single(editor.Targets).DisplayName);
        Assert.True(Assert.Single(editor.Targets).IsSelected);
        Assert.True(editor.HasUnresolvedTargets);
        Assert.Equal(new[] { "known-id", "missing-id" }, editor.AllowedTargetIds);
    }

    [Fact]
    public void Unchecking_a_project_does_not_clear_unresolved_authorization()
    {
        var editor = new AgentApprovedSourceViewModel(
            new AgentApprovedSource("codex", "instance-1", "Codex", "1", true,
                new[] { "known-id", "missing-id" }, true));
        editor.ApplyCatalog(new[] { new AgentTargetDescriptor("known-id", "Project A", false) });
        Assert.Single(editor.Targets).IsSelected = false;

        Assert.Equal(new[] { "missing-id" }, editor.AllowedTargetIds);
    }

    [Fact]
    public void Removing_unresolved_authorizations_is_explicit()
    {
        var editor = new AgentApprovedSourceViewModel(
            new AgentApprovedSource("codex", "instance-1", "Codex", "1", true,
                new[] { "missing-id" }, true));
        editor.ApplyCatalog(Array.Empty<AgentTargetDescriptor>());

        Assert.True(editor.RemoveUnresolvedTargets());
        Assert.Empty(editor.AllowedTargetIds);
    }

    private static AgentRelaySnapshot Snapshot(AgentPendingSource? pending = null, AgentApprovedSource? approved = null)
    {
        return new AgentRelaySnapshot(
            AgentRelayConnectionState.Connected,
            RelayOnline: true,
            AppOnline: true,
            AdapterOnline: approved?.IsOnline ?? false,
            DateTimeOffset.UtcNow,
            pending is null ? Array.Empty<AgentPendingSource>() : new[] { pending },
            approved is null ? Array.Empty<AgentApprovedSource>() : new[] { approved });
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public AppSettings Current { get; set; } = AppSettings.Defaults;
        public string Location => "test";
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public List<PersistedAgentConnection> Connections { get; } = new();
        public void SaveExecution(AgentExecution execution) { }
        public AgentExecution? GetExecution(string id) => null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => Array.Empty<AgentExecution>();
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => Array.Empty<AgentExecution>();
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => Array.Empty<AgentArchiveBatch>();
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Connections;
    }

    private sealed class FakeGateway : IAgentGateway
    {
        public bool IsConnected => true;
        public List<(string SourceType, bool Enabled)> SourceEnabledCalls { get; } = new();
        public List<(string SourceType, IReadOnlyList<string> TargetIds)> AllowedTargetCalls { get; } = new();
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(true, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AgentDispatchResult(AgentDispatchStatus.Accepted, request.DispatchRequestId));
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AgentOpenTaskResult(AgentOpenTaskStatus.AppOnly));
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(IReadOnlyList<AgentExecution> knownExecutions, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>(Array.Empty<AgentEvent>());
        public Task SetSourceEnabledAsync(string sourceType, bool enabled, CancellationToken cancellationToken = default)
        {
            SourceEnabledCalls.Add((sourceType, enabled));
            return Task.CompletedTask;
        }
        public Task SetAllowedTargetsAsync(string sourceType, IReadOnlyList<string> targetIds, CancellationToken cancellationToken = default)
        {
            AllowedTargetCalls.Add((sourceType, targetIds));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdministration : IAgentRelayAdministration
    {
        private AgentRelaySnapshot _snapshot;

        public FakeAdministration(AgentRelaySnapshot snapshot) => _snapshot = snapshot;
        public List<(string RequestId, bool Approve)> Decisions { get; } = new();
        public List<(string SourceType, string SourceInstanceId, IReadOnlyList<string> TargetIds, bool Enabled)> PermissionUpdates { get; } = new();
        public List<(string SourceType, string SourceInstanceId)> Revocations { get; } = new();
        public Task<AgentRelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
        public Task<AgentRelaySnapshot> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);
        public Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default)
        {
            Decisions.Add((requestId, approve));
            return Task.CompletedTask;
        }
        public Task UpdatePermissionsAsync(string sourceType, string sourceInstanceId, IReadOnlyList<string> targetIds, bool enabled, CancellationToken cancellationToken = default)
        {
            PermissionUpdates.Add((sourceType, sourceInstanceId, targetIds, enabled));
            return Task.CompletedTask;
        }
        public Task RevokeSourceAsync(string sourceType, string sourceInstanceId, CancellationToken cancellationToken = default)
        {
            Revocations.Add((sourceType, sourceInstanceId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntime : IAgentRelayRuntime
    {
        public FakeRuntime(AgentRelaySnapshot current) => Current = current;
        public AgentRelaySnapshot Current { get; private set; }
        public event Action<AgentRelaySnapshot>? SnapshotChanged;
        public List<bool> EnabledCalls { get; } = new();
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            EnabledCalls.Add(enabled);
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Publish(AgentRelaySnapshot snapshot)
        {
            Current = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }
    }
}
