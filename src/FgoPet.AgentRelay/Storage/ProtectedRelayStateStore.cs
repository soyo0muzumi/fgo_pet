using System.Security.Cryptography;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
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
        if (state.Inbound is null || state.Outbound is null || state.DispatchReceipts is null || state.InboundEventKeys is null
            || state.InboundEventWatermarks is null
            || state.Inbound.Count > RelayStore.MaxQueuedInboundEvents
            || state.Outbound.Count > RelayStore.MaxQueuedDispatches
            || state.DispatchReceipts.Count > RelayStore.MaxDispatchReceipts
            || state.InboundEventKeys.Count > RelayStore.MaxInboundEventKeys
            || state.InboundEventWatermarks.Count > RelayStore.MaxInboundWatermarks)
            return false;
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
            .All(group => group.Count() == 1)
            && state.Inbound.All(item => item is not null && item.Envelope is not null
                && string.Equals(item.Envelope.MessageType, "agent_event", StringComparison.Ordinal)
                && AgentProtocolValidator.IsValid(item.Envelope))
            && state.Outbound.All(item => item is not null
                && !string.IsNullOrWhiteSpace(item.SourceType)
                && !string.IsNullOrWhiteSpace(item.SourceInstance)
                && item.Request is not null
                && string.Equals(item.SourceType, item.Request.SourceType, StringComparison.Ordinal)
                && string.Equals(item.SourceInstance, item.Request.SourceInstanceId, StringComparison.Ordinal)
                && AgentProtocolValidator.IsValid(ProtocolEnvelope.Create(
                    "dispatch-" + item.Request.DispatchRequestId, "dispatch_task", item.Request, item.EnqueuedAt)))
            && !state.DispatchReceipts.Any(item => item is null)
            && state.DispatchReceipts.GroupBy(item => item.DispatchRequestId, StringComparer.Ordinal).All(group => group.Count() == 1)
            && state.DispatchReceipts.All(item => item is not null
                && item.DispatchRequestId is not null && item.Result is not null
                && item.SourceType is not null && item.SourceInstance is not null && item.RequestDigest is not null
                && IsSafeText(item.DispatchRequestId)
                && IsSafeText(item.Result)
                && (item.SourceType.Length == 0 || IsSafeText(item.SourceType))
                && (item.SourceInstance.Length == 0 || IsSafeText(item.SourceInstance))
                && (item.RequestDigest.Length == 0 || item.RequestDigest.Length == 64 && item.RequestDigest.All(IsHex)))
            && state.InboundEventKeys.All(IsStorageKey)
            && !state.InboundEventWatermarks.Any(item => item is null)
            && state.InboundEventWatermarks.GroupBy(item =>
                (item.SourceType, item.SourceInstance, item.TaskId))
                .All(group => group.Count() == 1)
            && state.InboundEventWatermarks.All(item => item is not null
                && IsSafeText(item.SourceType)
                && IsSafeText(item.SourceInstance)
                && IsSafeText(item.TaskId)
                && item.Sequence >= 1);
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

    private static bool IsSafeText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && value.All(character => !char.IsControl(character));

    private static bool IsStorageKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && value.All(character => !char.IsControl(character));
}
