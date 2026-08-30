using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Storage;
using FgoPet.Core.Agents;

namespace FgoPet.AgentRelay.Routing;

public enum RelayRouteResult
{
    Queued,
    Accepted,
    AlreadyApplied,
    Disabled,
    Offline,
    Unauthorized,
}

public sealed record RelayRouteReceipt(
    RelayRouteResult Result,
    string? DispatchRequestId = null,
    string? Error = null,
    string? TaskId = null,
    string? SourceInstance = null);

public sealed record RelayOpenReceipt(AgentOpenTaskStatus Status, string? Error = null);

public sealed class RelayRouter
{
    private readonly RelayStore _store;
    private readonly RegistrationService _registration;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, bool> _adapterOnline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _allowedTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _sourceEnabled = new(StringComparer.Ordinal);
    private bool _appOnline;

    public RelayRouter(RelayStore store, RegistrationService registration)
    {
        _store = store;
        _registration = registration;
    }

    public int PendingInboundCount => _store.PendingInboundCount;

    public void SetAppOnline(bool online) => _appOnline = online;

    public void SetAdapterOnline(string sourceType, string sourceInstance, bool online)
    {
        lock (_stateGate) _adapterOnline[$"{sourceType}/{sourceInstance}"] = online;
    }

    public void SetAllowedTargets(string sourceType, IEnumerable<string> targetIds)
    {
        lock (_stateGate) _allowedTargets[sourceType] = new HashSet<string>(targetIds, StringComparer.Ordinal);
    }

    public void ConfigureAllowedTargets(string credential, string sourceType, IEnumerable<string> targetIds, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        if (!string.Equals(grant.SourceType, sourceType, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The allowlist source identity does not match the registered adapter.");
        }

        SetAllowedTargets(sourceType, targetIds);
    }

    public void ConfigureSourceEnabled(string credential, string sourceType, bool enabled, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        if (!string.Equals(grant.SourceType, sourceType, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The source switch identity does not match the registered adapter.");
        }

        lock (_stateGate) _sourceEnabled[sourceType] = enabled;
    }

    public void SetConnectionEnabled(bool enabled) => _store.SetAcceptEvents(enabled);

    public void ClearPending() => _store.ClearPending();

    public RelayRouteReceipt RouteAdapterEvent(string credential, ProtocolEnvelope envelope, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        if (!string.Equals(envelope.MessageType, "agent_event", StringComparison.Ordinal))
        {
            throw new AgentProtocolValidationException("Adapter pipe accepts agent_event messages only.");
        }

        // Validate the raw envelope for unknown/denylisted fields, then validate the
        // sanitized copy that is the only representation allowed into the outbox.
        AgentProtocolValidator.Validate(envelope);
        var eventMessage = AgentPayloadSanitizer.Sanitize(envelope.DeserializePayload<AgentEventMessage>());
        if (!string.Equals(eventMessage.SourceType, grant.SourceType, StringComparison.Ordinal)
            || !string.Equals(eventMessage.SourceInstance, grant.SourceInstance, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The event source identity does not match the registered adapter.");
        }

        if (!_store.AcceptEvents || !IsSourceEnabled(grant.SourceType))
        {
            return new RelayRouteReceipt(RelayRouteResult.Disabled);
        }
        var sanitizedEnvelope = envelope with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(eventMessage, ProtocolEnvelope.JsonOptions) };
        AgentProtocolValidator.Validate(sanitizedEnvelope);
        var queued = _store.EnqueueInbound(sanitizedEnvelope, at);
        return new RelayRouteReceipt(queued ? RelayRouteResult.Queued : RelayRouteResult.AlreadyApplied);
    }

    public RelayRouteReceipt RouteDispatch(string credential, DispatchTaskRequest request, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        var envelope = ProtocolEnvelope.Create("dispatch-" + request.DispatchRequestId, "dispatch_task", request, at);
        AgentProtocolValidator.Validate(envelope);
        if (!_store.AcceptEvents || !_sourceEnabled.GetValueOrDefault(grant.SourceType, true))
        {
            return new RelayRouteReceipt(RelayRouteResult.Disabled, request.DispatchRequestId);
        }
        if (_store.GetDispatchReceipt(request.DispatchRequestId) is not null)
        {
            return new RelayRouteReceipt(RelayRouteResult.AlreadyApplied, request.DispatchRequestId);
        }

        if (!IsAdapterOnline(grant.SourceType, grant.SourceInstance))
        {
            return new RelayRouteReceipt(RelayRouteResult.Offline, request.DispatchRequestId, "adapter_offline");
        }

        if (!IsTargetAllowed(grant.SourceType, request.TargetId))
        {
            return new RelayRouteReceipt(RelayRouteResult.Unauthorized, request.DispatchRequestId, "target_not_allowed");
        }

        _store.EnqueueOutbound(new QueuedDispatch(grant.SourceType, grant.SourceInstance, request, at));
        _store.SaveDispatchReceipt(new DispatchReceipt(request.DispatchRequestId, RelayRouteResult.Accepted.ToString(), at));
        return new RelayRouteReceipt(RelayRouteResult.Accepted, request.DispatchRequestId, TaskId: request.DispatchRequestId, SourceInstance: grant.SourceInstance);
    }

    public IReadOnlyList<QueuedDispatch> DrainOutbound(string credential, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        return _store.DrainOutbound(grant.SourceType, grant.SourceInstance);
    }

    public RelayOpenReceipt RouteOpen(string credential, OpenTaskRequest request, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        if (!string.Equals(request.SourceType, grant.SourceType, StringComparison.Ordinal)
            || !string.Equals(request.SourceInstance, grant.SourceInstance, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The open-task source identity does not match the registered adapter.");
        }

        return IsAdapterOnline(grant.SourceType, grant.SourceInstance)
            ? new RelayOpenReceipt(AgentOpenTaskStatus.AppOnly, "exact_navigation_not_supported")
            : new RelayOpenReceipt(AgentOpenTaskStatus.Offline, "adapter_offline");
    }

    public IReadOnlyList<ProtocolEnvelope> DrainInbound()
    {
        var events = _store.DrainInbound();
        _appOnline = false;
        return events;
    }

    private bool IsSourceEnabled(string sourceType)
    {
        lock (_stateGate) return _sourceEnabled.GetValueOrDefault(sourceType, true);
    }

    private bool IsAdapterOnline(string sourceType, string sourceInstance)
    {
        lock (_stateGate) return _adapterOnline.GetValueOrDefault($"{sourceType}/{sourceInstance}");
    }

    private bool IsTargetAllowed(string sourceType, string targetId)
    {
        lock (_stateGate)
        {
            return _allowedTargets.TryGetValue(sourceType, out var targets) && targets.Contains(targetId);
        }
    }
}
