using FgoPet.AgentProtocol.Messages;

namespace FgoPet.CodexAdapter.Relay;

public enum AdapterConnectionStatus
{
    Connected,
    ApprovalRequired,
    Rejected,
    Revoked,
    RelayOffline,
    VersionMismatch,
}

public sealed record AdapterConnectionResult(AdapterConnectionStatus Status, string? RequestId = null, string? Error = null)
{
    public string StatusCode => Status switch
    {
        AdapterConnectionStatus.Connected => "connected",
        AdapterConnectionStatus.ApprovalRequired => "approval_required",
        AdapterConnectionStatus.Rejected => "rejected",
        AdapterConnectionStatus.Revoked => "revoked",
        AdapterConnectionStatus.VersionMismatch => "version_mismatch",
        _ => "relay_offline",
    };
}

public interface ICodexRelayConnector : ICodexRelaySession
{
    string SourceInstanceId { get; }
    Task<AdapterConnectionResult> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DispatchTaskRequest>> PollDispatchesAsync(CancellationToken cancellationToken = default);
    Task<bool> IsDispatchAllowedAsync(string targetId, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

public sealed class AdapterConnectionException(AdapterConnectionResult result) : IOException(result.StatusCode)
{
    public AdapterConnectionResult Result { get; } = result;
}
