using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.AgentRelay.Storage;

public sealed record PendingRegistration(
    string RequestId,
    string SourceType,
    string DisplayName,
    string Version,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset at) => at >= ExpiresAt;
}

public sealed record RegistrationGrant(
    string SourceType,
    string SourceInstance,
    string Credential,
    DateTimeOffset ApprovedAt);

public sealed record QueuedInboundEvent(ProtocolEnvelope Envelope, DateTimeOffset EnqueuedAt);

public sealed record DispatchReceipt(string DispatchRequestId, string Result, DateTimeOffset CreatedAt);

public sealed class RelayStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingRegistration> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationGrant> _grantsBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegistrationGrant> _grantsByCredential = new(StringComparer.Ordinal);
    private readonly Queue<QueuedInboundEvent> _inbound = new();
    private readonly HashSet<string> _inboundKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DispatchReceipt> _dispatchReceipts = new(StringComparer.Ordinal);

    public bool AcceptEvents { get; private set; } = true;

    public void AddPending(PendingRegistration registration)
    {
        lock (_gate) _pending[registration.RequestId] = registration;
    }

    public PendingRegistration? GetPending(string requestId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var registration)) return null;
            if (registration.IsExpired(at))
            {
                _pending.Remove(requestId);
                return null;
            }

            return registration;
        }
    }

    public void RemovePending(string requestId)
    {
        lock (_gate) _pending.Remove(requestId);
    }

    public void SaveGrant(RegistrationGrant grant)
    {
        lock (_gate)
        {
            if (_grantsBySource.TryGetValue(grant.SourceType, out var old))
            {
                _grantsByCredential.Remove(old.Credential);
            }

            _grantsBySource[grant.SourceType] = grant;
            _grantsByCredential[grant.Credential] = grant;
        }
    }

    public RegistrationGrant? Authenticate(string credential)
    {
        lock (_gate) return _grantsByCredential.GetValueOrDefault(credential);
    }

    public RegistrationGrant? GetGrant(string sourceType)
    {
        lock (_gate) return _grantsBySource.GetValueOrDefault(sourceType);
    }

    public bool Revoke(string sourceType)
    {
        lock (_gate)
        {
            if (!_grantsBySource.Remove(sourceType, out var grant)) return false;
            _grantsByCredential.Remove(grant.Credential);
            return true;
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

    public IReadOnlyList<ProtocolEnvelope> DrainInbound()
    {
        lock (_gate)
        {
            var result = _inbound.Select(item => item.Envelope).ToArray();
            _inbound.Clear();
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
            if (!enabled) _inbound.Clear();
        }
    }

    public DispatchReceipt? GetDispatchReceipt(string requestId)
    {
        lock (_gate) return _dispatchReceipts.GetValueOrDefault(requestId);
    }

    public void SaveDispatchReceipt(DispatchReceipt receipt)
    {
        lock (_gate) _dispatchReceipts.TryAdd(receipt.DispatchRequestId, receipt);
    }
}
