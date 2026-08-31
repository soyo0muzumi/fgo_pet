using System.Security.Cryptography;
using FgoPet.AgentRuntime.Security;
using FgoPet.AgentRuntime.Storage;

namespace FgoPet.AgentRelay.Storage;

public sealed class ProtectedRelayStateStore : IRelayStateStore
{
    public const string FileName = "relay-state.v1.json";
    private readonly AtomicProtectedJsonStore<RelayState> _store;

    public ProtectedRelayStateStore(string stateRoot, ISecretProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var root = Path.GetFullPath(stateRoot);
        _store = new AtomicProtectedJsonStore<RelayState>(
            Path.Combine(root, FileName), protector ?? new DpapiSecretProtector());
    }

    public RelayState Load() => _store.Load(
        () => RelayState.Empty,
        IsValidState);

    public void Save(RelayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != 1) throw new ArgumentException("Unsupported relay state schema.", nameof(state));
        _store.Save(state);
    }

    private static bool IsValidState(RelayState state)
    {
        if (state.SchemaVersion != 1 || state.Pending is null || state.Grants is null) return false;
        if (state.Pending.Any(item => item is null
            || string.IsNullOrWhiteSpace(item.RequestId)
            || string.IsNullOrWhiteSpace(item.SourceType)
            || string.IsNullOrWhiteSpace(item.SourceInstance)
            || item.ExpiresAt <= item.RequestedAt
            || !IsNonce(item.RequestNonce)
            || !string.Equals(item.Decision, "pending", StringComparison.Ordinal)
                && !string.Equals(item.Decision, "approved", StringComparison.Ordinal)
                && !string.Equals(item.Decision, "rejected", StringComparison.Ordinal)
                && !string.Equals(item.Decision, "revoked", StringComparison.Ordinal)
                && !string.Equals(item.Decision, "expired", StringComparison.Ordinal)
            || string.Equals(item.Decision, "approved", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(item.Credential)
            || !string.Equals(item.Decision, "approved", StringComparison.Ordinal) && item.Credential is not null))
            return false;
        if (state.Pending.GroupBy(item => item.RequestId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            return false;

        if (state.Grants.Any(item => item is null
            || string.IsNullOrWhiteSpace(item.SourceType)
            || string.IsNullOrWhiteSpace(item.SourceInstance)
            || string.IsNullOrWhiteSpace(item.Credential)
            || item.AllowedTargetIds is not null && item.AllowedTargetIds.Any(string.IsNullOrWhiteSpace)
            || item.RequestNonce is not null && !IsNonce(item.RequestNonce)
            || item.RequestId is not null && string.IsNullOrWhiteSpace(item.RequestId)
            || !IsCredential(item.Credential)))
            return false;
        return state.Grants.GroupBy(item => $"{item.SourceType}\u001f{item.SourceInstance}", StringComparer.Ordinal)
            .All(group => group.Count() == 1);
    }

    private static bool IsNonce(string? nonce) =>
        string.IsNullOrEmpty(nonce) || nonce.Length == 64 && nonce.All(IsHex);

    private static bool IsCredential(string credential)
    {
        try
        {
            var bytes = Convert.FromBase64String(credential);
            return bytes.Length == 32 && string.Equals(Convert.ToBase64String(bytes), credential, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
