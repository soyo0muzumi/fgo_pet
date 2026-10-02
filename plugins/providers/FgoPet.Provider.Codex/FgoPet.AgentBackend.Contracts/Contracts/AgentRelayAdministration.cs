namespace FgoPet.Core.Agents;

public enum AgentRelayConnectionState
{
    Disabled, RelayOffline, AwaitingApproval, AdapterOffline, AuthenticationFailed, VersionMismatch, Connected,
}

public sealed record AgentPendingSource(string RequestId, string SourceType, string SourceInstanceId,
    string DisplayName, string AdapterVersion, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record AgentApprovedSource(string SourceType, string SourceInstanceId, string DisplayName,
    string AdapterVersion, bool Enabled, IReadOnlyList<string> AllowedTargetIds, bool IsOnline);

public sealed record AgentMaintenanceCounter(string Name, int Used, int Limit, int Archivable)
{
    public bool IsNearCapacity => Limit > 0 && Used >= Limit * 0.8;
    public bool IsFull => Limit > 0 && Used >= Limit;
}

public sealed record AgentMaintenanceStatus(
    IReadOnlyList<AgentMaintenanceCounter> Counters,
    DateTimeOffset? OldestArchivableAt,
    string? ActiveBatchId,
    string? SafeError)
{
    public static AgentMaintenanceStatus Empty { get; } = new([], null, null, null);
}

public sealed record AgentArchivePrepareResult(
    string Result,
    string BatchId,
    string BatchSha256,
    string? SafeError = null);

public sealed record AgentArchiveCommitResult(
    string Result,
    string BatchId,
    string BatchSha256,
    string? SafeError = null);

public sealed record AgentRelaySnapshot(AgentRelayConnectionState State, bool RelayOnline, bool AppOnline,
    bool AdapterOnline, DateTimeOffset ObservedAtUtc, IReadOnlyList<AgentPendingSource> PendingSources,
    IReadOnlyList<AgentApprovedSource> Sources, string? SafeError = null)
{
    public static AgentRelaySnapshot Disabled { get; } = new(AgentRelayConnectionState.Disabled,
        false, false, false, DateTimeOffset.MinValue, [], []);
}

public interface IAgentRelayAdministration
{
    Task<AgentRelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<AgentRelaySnapshot> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default);
    Task UpdatePermissionsAsync(string sourceType, string sourceInstanceId, IReadOnlyList<string> targetIds, bool enabled, CancellationToken cancellationToken = default);
    Task RevokeSourceAsync(string sourceType, string sourceInstanceId, CancellationToken cancellationToken = default);
    Task<AgentMaintenanceStatus> GetMaintenanceStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AgentMaintenanceStatus.Empty);
    Task<AgentArchivePrepareResult> PrepareArchiveAsync(AgentArchiveBatch batch, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentArchivePrepareResult("rejected", batch.BatchId, batch.BatchSha256, "maintenance_unsupported"));
    Task<AgentArchiveCommitResult> CommitArchiveAsync(string batchId, string batchSha256, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentArchiveCommitResult("rejected", batchId, batchSha256, "maintenance_unsupported"));
}

public interface IAgentRelayRuntime
{
    AgentRelaySnapshot Current { get; }
    event Action<AgentRelaySnapshot>? SnapshotChanged;
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
