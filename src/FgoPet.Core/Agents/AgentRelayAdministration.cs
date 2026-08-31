namespace FgoPet.Core.Agents;

public enum AgentRelayConnectionState
{
    Disabled, RelayOffline, AwaitingApproval, AdapterOffline, AuthenticationFailed, VersionMismatch, Connected,
}

public sealed record AgentPendingSource(string RequestId, string SourceType, string SourceInstanceId,
    string DisplayName, string AdapterVersion, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record AgentApprovedSource(string SourceType, string SourceInstanceId, string DisplayName,
    string AdapterVersion, bool Enabled, IReadOnlyList<string> AllowedTargetIds, bool IsOnline);

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
}

public interface IAgentRelayRuntime
{
    AgentRelaySnapshot Current { get; }
    event Action<AgentRelaySnapshot>? SnapshotChanged;
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
