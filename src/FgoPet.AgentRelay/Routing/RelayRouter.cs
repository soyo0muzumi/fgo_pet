using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Storage;

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
    string? Error = null);

public sealed class RelayRouter
{
    private readonly RelayStore _store;
    private readonly RegistrationService _registration;
    private readonly Dictionary<string, bool> _adapterOnline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _allowedTargets = new(StringComparer.Ordinal);
    private bool _appOnline;

    public RelayRouter(RelayStore store, RegistrationService registration)
    {
        _store = store;
        _registration = registration;
    }

    public int PendingInboundCount => _store.PendingInboundCount;

    public void SetAppOnline(bool online) => _appOnline = online;

    public void SetAdapterOnline(string sourceType, string sourceInstance, bool online) =>
        _adapterOnline[$"{sourceType}/{sourceInstance}"] = online;

    public void SetAllowedTargets(string sourceType, IEnumerable<string> targetIds) =>
        _allowedTargets[sourceType] = new HashSet<string>(targetIds, StringComparer.Ordinal);

    public void SetConnectionEnabled(bool enabled) => _store.SetAcceptEvents(enabled);

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

        if (!_store.AcceptEvents) return new RelayRouteReceipt(RelayRouteResult.Disabled);
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
        if (!_store.AcceptEvents) return new RelayRouteReceipt(RelayRouteResult.Disabled, request.DispatchRequestId);
        if (_store.GetDispatchReceipt(request.DispatchRequestId) is not null)
        {
            return new RelayRouteReceipt(RelayRouteResult.AlreadyApplied, request.DispatchRequestId);
        }

        if (!_adapterOnline.GetValueOrDefault($"{grant.SourceType}/{grant.SourceInstance}"))
        {
            return new RelayRouteReceipt(RelayRouteResult.Offline, request.DispatchRequestId, "adapter_offline");
        }

        if (_allowedTargets.TryGetValue(grant.SourceType, out var targets) && !targets.Contains(request.TargetId))
        {
            return new RelayRouteReceipt(RelayRouteResult.Unauthorized, request.DispatchRequestId, "target_not_allowed");
        }

        _store.SaveDispatchReceipt(new DispatchReceipt(request.DispatchRequestId, RelayRouteResult.Accepted.ToString(), at));
        return new RelayRouteReceipt(RelayRouteResult.Accepted, request.DispatchRequestId);
    }

    public IReadOnlyList<ProtocolEnvelope> DrainInbound()
    {
        var events = _store.DrainInbound();
        _appOnline = false;
        return events;
    }
}
