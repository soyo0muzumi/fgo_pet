using System.Security.Cryptography;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.AgentRelay.Storage;

public sealed record PendingRegistration(
    string RequestId,
    string SourceType,
    string DisplayName,
    string Version,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string SourceInstance = "",
    string RequestNonce = "",
    string Decision = "pending",
    bool CredentialConsumed = false,
    string? Credential = null,
    DateTimeOffset? ApprovedAt = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceInstanceId => SourceInstance;

    [System.Text.Json.Serialization.JsonIgnore]
    public string AdapterVersion => Version;

    public bool IsExpired(DateTimeOffset at) => at >= ExpiresAt;
}

public sealed record RegistrationGrant(
    string SourceType,
    string SourceInstance,
    string Credential,
    DateTimeOffset ApprovedAt,
    bool Enabled = false,
    IReadOnlyList<string>? AllowedTargetIds = null,
    string DisplayName = "",
    string Version = "",
    string? RequestId = null,
    string RequestNonce = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceInstanceId => SourceInstance;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Targets => AllowedTargetIds ?? Array.Empty<string>();
}

public sealed record QueuedInboundEvent(ProtocolEnvelope Envelope, DateTimeOffset EnqueuedAt);

public sealed record DispatchReceipt(string DispatchRequestId, string Result, DateTimeOffset CreatedAt);
public sealed record QueuedDispatch(string SourceType, string SourceInstance, DispatchTaskRequest Request, DateTimeOffset EnqueuedAt);

/// <summary>
/// Relay state facade. Durable authorization changes are persisted before their in-memory
/// snapshot is published; queues, dedupe keys and online flags are deliberately transient.
/// </summary>
public sealed class RelayStore
{
    public const int MaxRegistrationRecords = 512;
    private readonly object _gate = new();
    private readonly IRelayStateStore _stateStore;
    private Dictionary<string, PendingRegistration> _pending;
    private Dictionary<string, RegistrationGrant> _grantsBySource;
    private readonly Queue<QueuedInboundEvent> _inbound = new();
    private readonly HashSet<string> _inboundKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DispatchReceipt> _dispatchReceipts = new(StringComparer.Ordinal);
    private readonly Queue<QueuedDispatch> _outbound = new();

    public RelayStore(IRelayStateStore? stateStore = null)
    {
        _stateStore = stateStore ?? new InMemoryRelayStateStore();
        var state = _stateStore.Load() ?? RelayState.Empty;
        if (state.SchemaVersion != 1 || state.Pending is null || state.Grants is null)
            throw new InvalidDataException("The relay state schema is invalid.");

        _pending = state.Pending.ToDictionary(item => item.RequestId, StringComparer.Ordinal);
        _grantsBySource = state.Grants.ToDictionary(item => SourceKey(item.SourceType, item.SourceInstance), StringComparer.Ordinal);
    }

    public bool AcceptEvents { get; private set; } = true;

    // Router decisions and store mutations use one re-entrant gate, so revoke
    // cannot land between authorization and enqueue/dequeue.
    internal object SyncRoot => _gate;
    internal T Execute<T>(Func<T> action) { lock (_gate) return action(); }

    public RelayState Snapshot
    {
        get { lock (_gate) return BuildState(_pending, _grantsBySource); }
    }

    public void AddPending(PendingRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            var next = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal)
            {
                [registration.RequestId] = registration,
            };
            CommitUnsafe(next, _grantsBySource);
        }
    }

    public PendingRegistration GetOrAddPending(
        string sourceType,
        string sourceInstance,
        string requestNonce,
        DateTimeOffset at,
        Func<PendingRegistration> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            var existing = _pending.Values.FirstOrDefault(item =>
                string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                && string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal)
                && string.Equals(item.RequestNonce, requestNonce, StringComparison.Ordinal));
            if (existing is not null) return existing;

            if (_pending.Count >= MaxRegistrationRecords
                || _pending.Values.Count(item => item.SourceType == sourceType && item.SourceInstance == sourceInstance
                    && item.Decision == "pending" && !item.IsExpired(at)) >= 8)
                throw new InvalidOperationException("registration_capacity");

            var registration = factory();
            var next = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal)
            {
                [registration.RequestId] = registration,
            };
            CommitUnsafe(next, _grantsBySource);
            return registration;
        }
    }

    public PendingRegistration? GetPending(string requestId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var registration)) return null;
            return registration.IsExpired(at) ? null : registration;
        }
    }

    public IReadOnlyList<PendingRegistration> ListPending(DateTimeOffset at)
    {
        lock (_gate)
        {
            return _pending.Values.Where(item =>
                    string.Equals(item.Decision, "pending", StringComparison.Ordinal)
                    && !item.IsExpired(at))
                .OrderBy(item => item.RequestedAt).ToArray();
        }
    }

    public void RemovePending(string requestId)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(requestId)) return;
            var next = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal);
            next.Remove(requestId);
            CommitUnsafe(next, _grantsBySource);
        }
    }

    public void SetPendingDecision(string requestId, string decision, RegistrationGrant? grant = null)
        => SetPendingDecision(requestId, decision, DateTimeOffset.UtcNow, grant);

    public void SetPendingDecision(string requestId, string decision, DateTimeOffset at, RegistrationGrant? grant = null)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var pending)
                || pending.IsExpired(at)
                || !string.Equals(pending.Decision, "pending", StringComparison.Ordinal))
                throw new InvalidOperationException("The pairing request is missing or expired.");
            var nextPending = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal)
            {
                [requestId] = pending with
                {
                    Decision = decision,
                    ApprovedAt = grant?.ApprovedAt ?? pending.ApprovedAt,
                    Credential = grant?.Credential ?? pending.Credential,
                },
            };
            CommitUnsafe(nextPending, _grantsBySource);
        }
    }

    public void ApprovePending(string requestId, RegistrationGrant grant, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var pending)
                || pending.IsExpired(at)
                || !string.Equals(pending.Decision, "pending", StringComparison.Ordinal))
                throw new InvalidOperationException("The pairing request is missing or expired.");
            if (_grantsBySource.ContainsKey(SourceKey(grant.SourceType, grant.SourceInstance)))
                throw new InvalidOperationException("Revoke the existing registration before pairing again.");
            var nextPending = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal)
            {
                [requestId] = pending with
                {
                    SourceInstance = grant.SourceInstance,
                    Decision = "approved",
                    ApprovedAt = grant.ApprovedAt,
                    Credential = grant.Credential,
                    CredentialConsumed = false,
                },
            };
            var nextGrants = new Dictionary<string, RegistrationGrant>(_grantsBySource, StringComparer.Ordinal)
            {
                [SourceKey(grant.SourceType, grant.SourceInstance)] = grant,
            };
            CommitUnsafe(nextPending, nextGrants);
        }
    }

    public void MarkCredentialConsumed(RegistrationGrant grant)
    {
        lock (_gate)
        {
            var pending = _pending.Values.FirstOrDefault(item =>
                string.Equals(item.SourceType, grant.SourceType, StringComparison.Ordinal)
                && string.Equals(item.SourceInstance, grant.SourceInstance, StringComparison.Ordinal)
                && (grant.RequestId is null || string.Equals(item.RequestId, grant.RequestId, StringComparison.Ordinal)));
            if (pending is null || pending.CredentialConsumed) return;
            var nextPending = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal)
            {
                [pending.RequestId] = pending with { CredentialConsumed = true },
            };
            CommitUnsafe(nextPending, _grantsBySource);
        }
    }

    public void SaveGrant(RegistrationGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            var next = new Dictionary<string, RegistrationGrant>(_grantsBySource, StringComparer.Ordinal)
            {
                [SourceKey(grant.SourceType, grant.SourceInstance)] = grant,
            };
            CommitUnsafe(_pending, next);
        }
    }

    public RegistrationGrant? Authenticate(string credential)
    {
        lock (_gate) return FindGrantByCredentialUnsafe(null, null, credential);
    }

    public RegistrationGrant? AuthenticateAndConsume(string credential)
    {
        lock (_gate)
        {
            var grant = FindGrantByCredentialUnsafe(null, null, credential);
            if (grant is null) return null;
            return ConsumeCredentialUnsafe(grant);
        }
    }

    public RegistrationGrant? Authenticate(string sourceType, string sourceInstance, string credential)
    {
        lock (_gate) return FindGrantByCredentialUnsafe(sourceType, sourceInstance, credential);
    }

    /// <summary>
    /// Authenticates and consumes the first-delivery marker as one state-machine
    /// transition. The revoked result is computed under the same gate so a revoke
    /// cannot race an authentication into an ambiguous outcome.
    /// </summary>
    public RegistrationGrant? AuthenticateAndConsume(
        string sourceType,
        string sourceInstance,
        string credential,
        out bool revoked)
    {
        lock (_gate)
        {
            var grant = FindGrantByCredentialUnsafe(sourceType, sourceInstance, credential);
            if (grant is null)
            {
                revoked = !_grantsBySource.ContainsKey(SourceKey(sourceType, sourceInstance))
                    && _pending.Values.Any(item =>
                        string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                        && string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal)
                        && string.Equals(item.Decision, "revoked", StringComparison.Ordinal));
                return null;
            }

            revoked = false;
            return ConsumeCredentialUnsafe(grant);
        }
    }

    public RegistrationGrant? AuthenticateAndConsume(string sourceType, string sourceInstance, string credential)
    {
        return AuthenticateAndConsume(sourceType, sourceInstance, credential, out _);
    }

    public RegistrationGrant? GetGrant(string sourceType)
    {
        lock (_gate)
        {
            var matches = _grantsBySource.Values.Where(item => string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    public RegistrationGrant? GetGrant(string sourceType, string sourceInstance)
    {
        lock (_gate) return _grantsBySource.GetValueOrDefault(SourceKey(sourceType, sourceInstance));
    }

    public bool IsRevoked(string sourceType, string sourceInstance)
    {
        lock (_gate)
        {
            if (_grantsBySource.ContainsKey(SourceKey(sourceType, sourceInstance))) return false;
            return _pending.Values.Any(item =>
                string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                && string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal)
                && string.Equals(item.Decision, "revoked", StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<RegistrationGrant> ListGrants()
    {
        lock (_gate) return _grantsBySource.Values.OrderBy(item => item.SourceType).ThenBy(item => item.SourceInstance).ToArray();
    }

    public bool Revoke(string sourceType) => Revoke(sourceType, sourceInstance: null);

    public bool Revoke(string sourceType, string? sourceInstance)
    {
        lock (_gate)
        {
            var keys = _grantsBySource.Values
                .Where(item => string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                    && (sourceInstance is null || string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal)))
                .Select(item => SourceKey(item.SourceType, item.SourceInstance))
                .ToArray();
            var pending = _pending.Values
                .Where(item => string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                    && (sourceInstance is null || string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal)))
                .ToArray();
            if (keys.Length == 0 && pending.Length == 0) return false;

            var nextGrants = new Dictionary<string, RegistrationGrant>(_grantsBySource, StringComparer.Ordinal);
            foreach (var key in keys) nextGrants.Remove(key);
            var nextPending = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal);
            foreach (var item in pending) nextPending[item.RequestId] = item with { Decision = "revoked", Credential = null };
            CommitUnsafe(nextPending, nextGrants);

            var retained = _outbound.Where(item =>
                !string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                || (sourceInstance is not null && !string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal))).ToArray();
            _outbound.Clear();
            foreach (var item in retained) _outbound.Enqueue(item);
            var retainedEvents = _inbound.Where(item =>
            {
                var message = item.Envelope.DeserializePayload<AgentEventMessage>();
                return message.SourceType != sourceType || sourceInstance is not null && message.SourceInstance != sourceInstance;
            }).ToArray();
            _inbound.Clear();
            foreach (var item in retainedEvents) _inbound.Enqueue(item);
            return keys.Length > 0;
        }
    }

    public void UpdatePermissions(string sourceType, string sourceInstance, IEnumerable<string> targetIds, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        lock (_gate)
        {
            var key = SourceKey(sourceType, sourceInstance);
            if (!_grantsBySource.TryGetValue(key, out var grant))
                throw new UnauthorizedAccessException("The source is not registered.");
            var next = new Dictionary<string, RegistrationGrant>(_grantsBySource, StringComparer.Ordinal)
            {
                [key] = grant with { Enabled = enabled, AllowedTargetIds = targetIds.Distinct(StringComparer.Ordinal).ToArray() },
            };
            CommitUnsafe(_pending, next);
        }
    }

    public bool EnqueueInbound(ProtocolEnvelope envelope, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!AcceptEvents) return false;
            var key = envelope.MessageId;
            if (envelope.MessageType == "agent_event")
            {
                var message = envelope.DeserializePayload<AgentEventMessage>();
                key = $"{message.SourceType}/{message.SourceInstance}/{message.TaskId}/{message.Sequence}";
            }

            if (!_inboundKeys.Add(key)) return false;
            _inbound.Enqueue(new QueuedInboundEvent(envelope, at));
            return true;
        }
    }

    public IReadOnlyList<ProtocolEnvelope> DrainInbound(int maxBytes = int.MaxValue, bool consume = true)
    {
        lock (_gate)
        {
            var result = new List<ProtocolEnvelope>();
            var bytes = 0;
            foreach (var item in _inbound)
            {
                var size = System.Text.Encoding.UTF8.GetByteCount(item.Envelope.ToJson()) + 1;
                if (size > maxBytes) throw new InvalidDataException("queued_event_too_large");
                if (bytes + size > maxBytes) break;
                result.Add(item.Envelope);
                bytes += size;
            }
            if (consume) for (var index = 0; index < result.Count; index++) _inbound.Dequeue();
            return result;
        }
    }

    public int PendingInboundCount
    {
        get { lock (_gate) return _inbound.Count; }
    }

    public void SetAcceptEvents(bool enabled)
    {
        lock (_gate)
        {
            AcceptEvents = enabled;
            if (!enabled) ClearPendingUnsafe(clearDeduplication: true);
        }
    }

    public void ClearPending()
    {
        lock (_gate) ClearPendingUnsafe(clearDeduplication: true);
    }

    public DispatchReceipt? GetDispatchReceipt(string requestId)
    {
        lock (_gate) return _dispatchReceipts.GetValueOrDefault(requestId);
    }

    public void SaveDispatchReceipt(DispatchReceipt receipt)
    {
        lock (_gate) _dispatchReceipts.TryAdd(receipt.DispatchRequestId, receipt);
    }

    /// <summary>Queues a dispatch and records its idempotency receipt in one gate.</summary>
    public bool TryEnqueueOutbound(QueuedDispatch dispatch, out DispatchReceipt? existing)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate)
        {
            if (_dispatchReceipts.TryGetValue(dispatch.Request.DispatchRequestId, out existing)) return false;
            var receipt = new DispatchReceipt(
                dispatch.Request.DispatchRequestId,
                "Accepted",
                dispatch.EnqueuedAt);
            _outbound.Enqueue(dispatch);
            _dispatchReceipts[receipt.DispatchRequestId] = receipt;
            existing = null;
            return true;
        }
    }

    public void EnqueueOutbound(QueuedDispatch dispatch)
    {
        lock (_gate) _outbound.Enqueue(dispatch);
    }

    public IReadOnlyList<QueuedDispatch> DrainOutbound(string sourceType, string sourceInstance, int maxBytes = int.MaxValue, bool consume = true)
    {
        lock (_gate)
        {
            var matching = new List<QueuedDispatch>();
            var bytes = 0;
            foreach (var item in _outbound.Where(item => item.SourceType == sourceType && item.SourceInstance == sourceInstance))
            {
                var size = System.Text.Encoding.UTF8.GetByteCount(ProtocolEnvelope.Create(
                    "dispatch-" + item.Request.DispatchRequestId, "dispatch_task", item.Request, item.EnqueuedAt).ToJson()) + 1;
                if (size > maxBytes) throw new InvalidDataException("queued_dispatch_too_large");
                if (bytes + size > maxBytes) break;
                matching.Add(item);
                bytes += size;
            }
            if (matching.Count == 0) return matching;
            if (!consume) return matching;

            var retained = _outbound
                .Where(item => !matching.Contains(item))
                .ToArray();
            _outbound.Clear();
            foreach (var item in retained) _outbound.Enqueue(item);
            return matching;
        }
    }

    internal void CompleteInboundBatch(IEnumerable<AgentEventMessage> messages)
    {
        lock (_gate)
        {
            var keys = messages.Select(item => (item.SourceType, item.SourceInstance, item.TaskId, item.Sequence)).ToHashSet();
            var remaining = _inbound.Where(item =>
            {
                var message = item.Envelope.DeserializePayload<AgentEventMessage>();
                return !keys.Contains((message.SourceType, message.SourceInstance, message.TaskId, message.Sequence));
            }).ToArray();
            _inbound.Clear();
            foreach (var item in remaining) _inbound.Enqueue(item);
        }
    }

    internal void CompleteOutboundBatch(IEnumerable<string> requestIds)
    {
        lock (_gate)
        {
            var ids = requestIds.ToHashSet(StringComparer.Ordinal);
            var remaining = _outbound.Where(item => !ids.Contains(item.Request.DispatchRequestId)).ToArray();
            _outbound.Clear();
            foreach (var item in remaining) _outbound.Enqueue(item);
        }
    }

    private RegistrationGrant? FindGrantByCredentialUnsafe(string? sourceType, string? sourceInstance, string credential)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(credential); }
        catch (FormatException) { return null; }
        if (supplied.Length != 32) return null;

        foreach (var grant in _grantsBySource.Values)
        {
            if (sourceType is not null && !string.Equals(grant.SourceType, sourceType, StringComparison.Ordinal)) continue;
            if (sourceInstance is not null && !string.Equals(grant.SourceInstance, sourceInstance, StringComparison.Ordinal)) continue;
            byte[] expected;
            try { expected = Convert.FromBase64String(grant.Credential); }
            catch (FormatException) { continue; }
            if (expected.Length == 32 && CryptographicOperations.FixedTimeEquals(supplied, expected)) return grant;
        }

        return null;
    }

    private RegistrationGrant ConsumeCredentialUnsafe(RegistrationGrant grant)
    {
        var pending = _pending.Values.FirstOrDefault(item =>
            string.Equals(item.SourceType, grant.SourceType, StringComparison.Ordinal)
            && string.Equals(item.SourceInstance, grant.SourceInstance, StringComparison.Ordinal)
            && (grant.RequestId is null || string.Equals(item.RequestId, grant.RequestId, StringComparison.Ordinal)));
        if (pending is null || pending.CredentialConsumed) return grant;

        var nextPending = new Dictionary<string, PendingRegistration>(_pending, StringComparer.Ordinal)
        {
            [pending.RequestId] = pending with { CredentialConsumed = true },
        };
        CommitUnsafe(nextPending, _grantsBySource);
        return grant;
    }

    private void CommitUnsafe(
        Dictionary<string, PendingRegistration> nextPending,
        Dictionary<string, RegistrationGrant> nextGrants)
    {
        var nextState = BuildState(nextPending, nextGrants);
        _stateStore.Save(nextState);
        _pending = nextPending;
        _grantsBySource = nextGrants;
    }

    private static RelayState BuildState(
        IReadOnlyDictionary<string, PendingRegistration> pending,
        IReadOnlyDictionary<string, RegistrationGrant> grants) =>
        new(1,
            pending.Values.OrderBy(item => item.RequestedAt).ToArray(),
            grants.Values.OrderBy(item => item.SourceType).ThenBy(item => item.SourceInstance).ToArray());

    private static string SourceKey(string sourceType, string sourceInstance) => $"{sourceType}\u001f{sourceInstance}";

    private void ClearPendingUnsafe(bool clearDeduplication)
    {
        _inbound.Clear();
        _outbound.Clear();
        if (clearDeduplication) _inboundKeys.Clear();
    }
}
