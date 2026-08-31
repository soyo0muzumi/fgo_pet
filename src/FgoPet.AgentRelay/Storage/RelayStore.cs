using System.Security.Cryptography;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;

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
public sealed record InboundEventWatermark(string SourceType, string SourceInstance, string TaskId, long Sequence);

public sealed record DispatchReceipt(
    string DispatchRequestId,
    string Result,
    DateTimeOffset CreatedAt,
    string SourceType = "",
    string SourceInstance = "",
    string RequestDigest = "",
    bool Acknowledged = false);
public sealed record QueuedDispatch(string SourceType, string SourceInstance, DispatchTaskRequest Request, DateTimeOffset EnqueuedAt);

/// <summary>
/// Relay state facade. Durable authorization changes are persisted before their in-memory
/// snapshot is published; online flags remain transient while delivery queues and
/// idempotency state are part of the durable snapshot.
/// </summary>
public sealed class RelayStore
{
    public const int MaxRegistrationRecords = 512;
    public const int MaxQueuedInboundEvents = 512;
    public const int MaxQueuedDispatches = 512;
    public const int MaxDispatchReceipts = 4096;
    public const int MaxInboundEventKeys = 4096;
    public const int MaxInboundWatermarks = 4096;
    private readonly object _gate = new();
    private readonly IRelayStateStore _stateStore;
    private Dictionary<string, PendingRegistration> _pending;
    private Dictionary<string, RegistrationGrant> _grantsBySource;
    private Queue<QueuedInboundEvent> _inbound;
    // Keys cover only currently queued events; sequence watermarks cover
    // acknowledged events without retaining one key per delivery forever.
    private HashSet<string> _inboundKeys;
    private Dictionary<string, InboundEventWatermark> _inboundWatermarks;
    private Dictionary<string, DispatchReceipt> _dispatchReceipts;
    private Queue<QueuedDispatch> _outbound;

    public RelayStore(IRelayStateStore? stateStore = null)
    {
        _stateStore = stateStore ?? new InMemoryRelayStateStore();
        var state = _stateStore.Load() ?? RelayState.Empty;
        if (state.SchemaVersion != 1 || state.Pending is null || state.Grants is null
            || state.Inbound is null || state.Outbound is null || state.DispatchReceipts is null || state.InboundEventKeys is null
            || state.InboundEventWatermarks is null
            || state.Inbound.Count > MaxQueuedInboundEvents || state.Outbound.Count > MaxQueuedDispatches
            || state.DispatchReceipts.Count > MaxDispatchReceipts
            || state.InboundEventWatermarks.Count > MaxInboundWatermarks)
            throw new InvalidDataException("The relay state schema is invalid.");

        _pending = state.Pending.ToDictionary(item => item.RequestId, StringComparer.Ordinal);
        _grantsBySource = state.Grants.ToDictionary(item => SourceKey(item.SourceType, item.SourceInstance), StringComparer.Ordinal);
        _inbound = new Queue<QueuedInboundEvent>(state.Inbound ?? Array.Empty<QueuedInboundEvent>());
        _outbound = new Queue<QueuedDispatch>(state.Outbound ?? Array.Empty<QueuedDispatch>());
        var persistedReceipts = state.DispatchReceipts ?? Array.Empty<DispatchReceipt>();
        if (persistedReceipts.Any(item => item is null || string.IsNullOrWhiteSpace(item.DispatchRequestId)))
            throw new InvalidDataException("The relay dispatch receipt state is invalid.");
        _dispatchReceipts = persistedReceipts.ToDictionary(item => item.DispatchRequestId, StringComparer.Ordinal);
        _inboundKeys = new HashSet<string>(StringComparer.Ordinal);
        _inboundWatermarks = new Dictionary<string, InboundEventWatermark>(StringComparer.Ordinal);
        foreach (var watermark in state.InboundEventWatermarks)
        {
            if (watermark is null || string.IsNullOrWhiteSpace(watermark.SourceType)
                || string.IsNullOrWhiteSpace(watermark.SourceInstance)
                || string.IsNullOrWhiteSpace(watermark.TaskId)
                || watermark.Sequence < 1)
                throw new InvalidDataException("The relay inbound watermark state is invalid.");
            var taskKey = TaskKey(watermark.SourceType, watermark.SourceInstance, watermark.TaskId);
            if (_inboundWatermarks.TryGetValue(taskKey, out var existing)
                && existing.Sequence >= watermark.Sequence)
                continue;
            _inboundWatermarks[taskKey] = watermark;
        }
        foreach (var item in _inbound)
        {
            if (item is null || item.Envelope is null || item.Envelope.MessageType != "agent_event")
                throw new InvalidDataException("The relay inbound state is invalid.");
            AgentProtocolValidator.Validate(item.Envelope);
            var message = item.Envelope.DeserializePayload<AgentEventMessage>();
            _inboundKeys.Add(EventKey(message));
            SetWatermark(_inboundWatermarks, message);
        }
        foreach (var item in _outbound)
        {
            if (item is null || item.Request is null
                || !string.Equals(item.SourceType, item.Request.SourceType, StringComparison.Ordinal)
                || !string.Equals(item.SourceInstance, item.Request.SourceInstanceId, StringComparison.Ordinal))
                throw new InvalidDataException("The relay outbound state is invalid.");
            AgentProtocolValidator.Validate(ProtocolEnvelope.Create(
                "dispatch-" + item.Request.DispatchRequestId, "dispatch_task", item.Request, item.EnqueuedAt));
        }
    }

    public bool AcceptEvents { get; private set; } = true;

    // Router decisions and store mutations use one re-entrant gate, so revoke
    // cannot land between authorization and enqueue/dequeue.
    internal object SyncRoot => _gate;
    internal T Execute<T>(Func<T> action) { lock (_gate) return action(); }

    public RelayState Snapshot
    {
        get { lock (_gate) return BuildState(_pending, _grantsBySource, _inbound, _outbound, _dispatchReceipts.Values, _inboundKeys, _inboundWatermarks.Values); }
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
            var retained = _outbound.Where(item =>
                !string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                || (sourceInstance is not null && !string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal))).ToArray();
            var retainedEvents = _inbound.Where(item =>
            {
                var message = item.Envelope.DeserializePayload<AgentEventMessage>();
                return message.SourceType != sourceType || sourceInstance is not null && message.SourceInstance != sourceInstance;
            }).ToArray();
            var nextReceipts = new Dictionary<string, DispatchReceipt>(_dispatchReceipts, StringComparer.Ordinal);
            foreach (var receipt in _dispatchReceipts.Values.Where(item =>
                         string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
                         && (sourceInstance is null || string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal))))
            {
                nextReceipts[receipt.DispatchRequestId] = receipt with { Result = "revoked", Acknowledged = true };
            }
            var nextKeys = retainedEvents.Select(item => EventKey(item.Envelope.DeserializePayload<AgentEventMessage>()))
                .ToHashSet(StringComparer.Ordinal);
            CommitRuntimeUnsafe(nextPending, nextGrants,
                new Queue<QueuedInboundEvent>(retainedEvents),
                new Queue<QueuedDispatch>(retained),
                nextReceipts,
                nextKeys,
                _inboundWatermarks);
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
            AgentEventMessage? message = null;
            if (envelope.MessageType == "agent_event")
            {
                message = envelope.DeserializePayload<AgentEventMessage>();
                key = EventKey(message);
            }

            if (_inboundKeys.Contains(key)
                || message is not null
                    && _inboundWatermarks.TryGetValue(TaskKey(message), out var watermark)
                    && message.Sequence <= watermark.Sequence)
                return false;
            if (_inbound.Count >= MaxQueuedInboundEvents) throw new InvalidDataException("relay_inbound_queue_full");
            var nextKeys = new HashSet<string>(_inboundKeys, StringComparer.Ordinal) { key };
            var nextWatermarks = new Dictionary<string, InboundEventWatermark>(_inboundWatermarks, StringComparer.Ordinal);
            if (message is not null && !nextWatermarks.ContainsKey(TaskKey(message)))
            {
                if (nextWatermarks.Count >= MaxInboundWatermarks)
                    throw new InvalidDataException("relay_event_watermarks_full");
            }
            if (message is not null) SetWatermark(nextWatermarks, message);
            var nextInbound = new Queue<QueuedInboundEvent>(_inbound);
            nextInbound.Enqueue(new QueuedInboundEvent(envelope, at));
            CommitRuntimeUnsafe(_pending, _grantsBySource, nextInbound, _outbound, _dispatchReceipts, nextKeys, nextWatermarks);
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
            if (consume && result.Count > 0)
            {
                var nextInbound = new Queue<QueuedInboundEvent>(_inbound);
                for (var index = 0; index < result.Count; index++) nextInbound.Dequeue();
                var nextKeys = nextInbound.Select(item => EventKey(item.Envelope.DeserializePayload<AgentEventMessage>()))
                    .ToHashSet(StringComparer.Ordinal);
                CommitRuntimeUnsafe(_pending, _grantsBySource, nextInbound, _outbound, _dispatchReceipts, nextKeys, _inboundWatermarks);
            }
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
            if (!enabled)
            {
                CommitRuntimeUnsafe(_pending, _grantsBySource,
                    new Queue<QueuedInboundEvent>(), new Queue<QueuedDispatch>(),
                    new Dictionary<string, DispatchReceipt>(StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal),
                    new Dictionary<string, InboundEventWatermark>(StringComparer.Ordinal));
            }
            AcceptEvents = enabled;
        }
    }

    public void ClearPending()
    {
        lock (_gate)
        {
            CommitRuntimeUnsafe(_pending, _grantsBySource,
                new Queue<QueuedInboundEvent>(), new Queue<QueuedDispatch>(),
                new Dictionary<string, DispatchReceipt>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, InboundEventWatermark>(StringComparer.Ordinal));
        }
    }

    public DispatchReceipt? GetDispatchReceipt(string requestId)
    {
        lock (_gate) return _dispatchReceipts.GetValueOrDefault(requestId);
    }

    public void SaveDispatchReceipt(DispatchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (_gate)
        {
            if (_dispatchReceipts.ContainsKey(receipt.DispatchRequestId)) return;
            if (_dispatchReceipts.Count >= MaxDispatchReceipts) throw new InvalidDataException("relay_dispatch_receipts_full");
            var next = new Dictionary<string, DispatchReceipt>(_dispatchReceipts, StringComparer.Ordinal)
            {
                [receipt.DispatchRequestId] = receipt,
            };
            CommitRuntimeUnsafe(_pending, _grantsBySource, _inbound, _outbound, next, _inboundKeys);
        }
    }

    /// <summary>Queues a dispatch and records its idempotency receipt in one gate.</summary>
    public bool TryEnqueueOutbound(QueuedDispatch dispatch, out DispatchReceipt? existing)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate)
        {
            if (_dispatchReceipts.TryGetValue(dispatch.Request.DispatchRequestId, out existing)) return false;
            if (_outbound.Count >= MaxQueuedDispatches) throw new InvalidDataException("relay_outbound_queue_full");
            if (_dispatchReceipts.Count >= MaxDispatchReceipts) throw new InvalidDataException("relay_dispatch_receipts_full");
            var receipt = new DispatchReceipt(
                dispatch.Request.DispatchRequestId,
                "accepted",
                dispatch.EnqueuedAt,
                dispatch.SourceType,
                dispatch.SourceInstance,
                DispatchDigest(dispatch.Request));
            var nextOutbound = new Queue<QueuedDispatch>(_outbound);
            nextOutbound.Enqueue(dispatch);
            var nextReceipts = new Dictionary<string, DispatchReceipt>(_dispatchReceipts, StringComparer.Ordinal)
            {
                [receipt.DispatchRequestId] = receipt,
            };
            CommitRuntimeUnsafe(_pending, _grantsBySource, _inbound, nextOutbound, nextReceipts, _inboundKeys);
            existing = null;
            return true;
        }
    }

    public void EnqueueOutbound(QueuedDispatch dispatch)
    {
        if (!TryEnqueueOutbound(dispatch, out var existing) && existing is not null)
            throw new InvalidOperationException("dispatch_already_exists");
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
            var nextOutbound = new Queue<QueuedDispatch>(retained);
            var nextReceipts = new Dictionary<string, DispatchReceipt>(_dispatchReceipts, StringComparer.Ordinal);
            foreach (var item in matching)
            {
                if (nextReceipts.TryGetValue(item.Request.DispatchRequestId, out var receipt))
                    nextReceipts[item.Request.DispatchRequestId] = receipt with { Acknowledged = true, Result = "acknowledged" };
            }
            CommitRuntimeUnsafe(_pending, _grantsBySource, _inbound, nextOutbound, nextReceipts, _inboundKeys);
            return matching;
        }
    }

    public string AcknowledgeInbound(string sourceType, string sourceInstance, IEnumerable<(string TaskId, long Sequence)> eventKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        ArgumentNullException.ThrowIfNull(eventKeys);
        lock (_gate)
        {
            var keys = eventKeys.Select(item => EventKey(sourceType, sourceInstance, item.TaskId, item.Sequence))
                .ToHashSet(StringComparer.Ordinal);
            var retained = _inbound.Where(item =>
            {
                var message = item.Envelope.DeserializePayload<AgentEventMessage>();
                return !keys.Contains(EventKey(message));
            }).ToArray();
            var removed = _inbound.Count - retained.Length;
            if (removed > 0)
            {
                var nextKeys = retained.Select(item => EventKey(item.Envelope.DeserializePayload<AgentEventMessage>()))
                    .ToHashSet(StringComparer.Ordinal);
                CommitRuntimeUnsafe(_pending, _grantsBySource, new Queue<QueuedInboundEvent>(retained),
                    _outbound, _dispatchReceipts, nextKeys, _inboundWatermarks);
            }
            return removed > 0 ? "acknowledged" : "already_acknowledged";
        }
    }

    public string AcknowledgeOutbound(string sourceType, string sourceInstance, IEnumerable<string> requestIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        ArgumentNullException.ThrowIfNull(requestIds);
        lock (_gate)
        {
            var ids = requestIds.ToHashSet(StringComparer.Ordinal);
            var matching = _outbound.Where(item => item.SourceType == sourceType
                && item.SourceInstance == sourceInstance
                && ids.Contains(item.Request.DispatchRequestId)).ToArray();
            var retained = _outbound.Where(item => !matching.Contains(item)).ToArray();
            var nextReceipts = new Dictionary<string, DispatchReceipt>(_dispatchReceipts, StringComparer.Ordinal);
            foreach (var item in matching)
            {
                if (nextReceipts.TryGetValue(item.Request.DispatchRequestId, out var receipt))
                    nextReceipts[item.Request.DispatchRequestId] = receipt with { Acknowledged = true, Result = "acknowledged" };
            }
            if (matching.Length > 0)
                CommitRuntimeUnsafe(_pending, _grantsBySource, _inbound, new Queue<QueuedDispatch>(retained), nextReceipts, _inboundKeys);
            return matching.Length > 0 ? "acknowledged" : "already_acknowledged";
        }
    }

    internal void CompleteInboundBatch(IEnumerable<AgentEventMessage> messages)
    {
        foreach (var group in messages.GroupBy(item => (item.SourceType, item.SourceInstance)))
            AcknowledgeInbound(group.Key.SourceType, group.Key.SourceInstance,
                group.Select(item => (item.TaskId, item.Sequence)));
    }

    internal void CompleteOutboundBatch(IEnumerable<string> requestIds)
    {
        lock (_gate)
        {
            var ids = requestIds.ToHashSet(StringComparer.Ordinal);
            var remaining = _outbound.Where(item => !ids.Contains(item.Request.DispatchRequestId)).ToArray();
            if (remaining.Length == _outbound.Count) return;
            var nextReceipts = new Dictionary<string, DispatchReceipt>(_dispatchReceipts, StringComparer.Ordinal);
            foreach (var id in ids)
                if (nextReceipts.TryGetValue(id, out var receipt)) nextReceipts[id] = receipt with { Acknowledged = true, Result = "acknowledged" };
            CommitRuntimeUnsafe(_pending, _grantsBySource, _inbound, new Queue<QueuedDispatch>(remaining), nextReceipts, _inboundKeys);
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
        CommitRuntimeUnsafe(nextPending, nextGrants, _inbound, _outbound, _dispatchReceipts, _inboundKeys);
    }

    private RelayState BuildState(
        IReadOnlyDictionary<string, PendingRegistration> pending,
        IReadOnlyDictionary<string, RegistrationGrant> grants,
        IEnumerable<QueuedInboundEvent> inbound,
        IEnumerable<QueuedDispatch> outbound,
        IEnumerable<DispatchReceipt> dispatchReceipts,
        IEnumerable<string> inboundKeys,
        IEnumerable<InboundEventWatermark> inboundWatermarks) =>
        new(1,
            pending.Values.OrderBy(item => item.RequestedAt).ToArray(),
            grants.Values.OrderBy(item => item.SourceType).ThenBy(item => item.SourceInstance).ToArray(),
            inbound.ToArray(), outbound.ToArray(), dispatchReceipts.ToArray(), inboundKeys.ToArray(), inboundWatermarks.ToArray());

    private static string SourceKey(string sourceType, string sourceInstance) => $"{sourceType}\u001f{sourceInstance}";

    private void CommitRuntimeUnsafe(
        Dictionary<string, PendingRegistration> nextPending,
        Dictionary<string, RegistrationGrant> nextGrants,
        Queue<QueuedInboundEvent> nextInbound,
        Queue<QueuedDispatch> nextOutbound,
        Dictionary<string, DispatchReceipt> nextReceipts,
        HashSet<string> nextInboundKeys,
        Dictionary<string, InboundEventWatermark>? nextInboundWatermarks = null)
    {
        nextInboundWatermarks ??= _inboundWatermarks;
        if (nextInbound.Count > MaxQueuedInboundEvents) throw new InvalidDataException("relay_inbound_queue_full");
        if (nextOutbound.Count > MaxQueuedDispatches) throw new InvalidDataException("relay_outbound_queue_full");
        if (nextReceipts.Count > MaxDispatchReceipts) throw new InvalidDataException("relay_dispatch_receipts_full");
        if (nextInboundKeys.Count > MaxInboundEventKeys) throw new InvalidDataException("relay_event_deduplication_full");
        if (nextInboundWatermarks.Count > MaxInboundWatermarks) throw new InvalidDataException("relay_event_watermarks_full");
        _stateStore.Save(BuildState(nextPending, nextGrants, nextInbound, nextOutbound, nextReceipts.Values, nextInboundKeys, nextInboundWatermarks.Values));
        _pending = nextPending;
        _grantsBySource = nextGrants;
        _inbound = nextInbound;
        _outbound = nextOutbound;
        _dispatchReceipts = nextReceipts;
        _inboundKeys = nextInboundKeys;
        _inboundWatermarks = nextInboundWatermarks;
    }

    private static string EventKey(AgentEventMessage message) =>
        EventKey(message.SourceType, message.SourceInstance, message.TaskId, message.Sequence);

    private static string EventKey(string sourceType, string sourceInstance, string taskId, long sequence) =>
        System.Text.Json.JsonSerializer.Serialize(new object[] { sourceType, sourceInstance, taskId, sequence }, ProtocolEnvelope.JsonOptions);

    private static string TaskKey(AgentEventMessage message) =>
        TaskKey(message.SourceType, message.SourceInstance, message.TaskId);

    private static string TaskKey(string sourceType, string sourceInstance, string taskId) =>
        System.Text.Json.JsonSerializer.Serialize(new[] { sourceType, sourceInstance, taskId }, ProtocolEnvelope.JsonOptions);

    private static void SetWatermark(Dictionary<string, InboundEventWatermark> watermarks, AgentEventMessage message)
    {
        var key = TaskKey(message);
        if (!watermarks.TryGetValue(key, out var existing) || existing.Sequence < message.Sequence)
            watermarks[key] = new InboundEventWatermark(message.SourceType, message.SourceInstance, message.TaskId, message.Sequence);
    }

    internal static string DispatchDigest(DispatchTaskRequest request) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(request, ProtocolEnvelope.JsonOptions))));
}
