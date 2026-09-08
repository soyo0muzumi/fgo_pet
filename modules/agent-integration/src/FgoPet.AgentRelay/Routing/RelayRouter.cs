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
    Backpressure,
}

public sealed record RelayRouteReceipt(
    RelayRouteResult Result,
    string? DispatchRequestId = null,
    string? Error = null,
    string? TaskId = null,
    string? SourceInstance = null);

public sealed record RelayOpenReceipt(AgentOpenTaskStatus Status, string? Error = null);

public sealed record RelayMaintenanceResponse(
    string Result,
    string? BatchId = null,
    IReadOnlyList<AgentArchiveProtocolItem>? Items = null,
    string? BatchSha256 = null,
    string? AcknowledgedBatchId = null,
    string? AcknowledgedPhase = null,
    string? SafeError = null,
    string? SourceType = null,
    string? SourceInstance = null);

public sealed class RelayRouter
{
    private static readonly TimeSpan AdapterLease = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AppLease = TimeSpan.FromSeconds(10);
    private readonly RelayStore _store;
    private readonly RegistrationService _registration;
    private readonly object _stateGate;
    private readonly Dictionary<string, (DateTimeOffset At, string Credential)> _adapterLastSeen = new(StringComparer.Ordinal);
    private DateTimeOffset? _appLastSeen;

    public RelayRouter(RelayStore store, RegistrationService registration)
    {
        _store = store;
        _registration = registration;
        _stateGate = store.SyncRoot;
    }

    public int PendingInboundCount => _store.PendingInboundCount;

    public void SetAppOnline(bool online)
    {
        lock (_stateGate)
        {
            _appLastSeen = online ? DateTimeOffset.UtcNow : null;
        }
    }

    public void TouchAppOnline(DateTimeOffset at)
    {
        lock (_stateGate) _appLastSeen = at;
    }

    public bool IsAppOnline(DateTimeOffset at)
    {
        lock (_stateGate)
        {
            return _appLastSeen is not null && at >= _appLastSeen.Value && at - _appLastSeen.Value <= AppLease;
        }
    }

    public void SetAdapterOnline(string sourceType, string sourceInstance, bool online)
    {
        lock (_stateGate)
        {
            var key = SourceKey(sourceType, sourceInstance);
            if (online) TouchAdapterOnline(sourceType, sourceInstance, DateTimeOffset.UtcNow);
            else _adapterLastSeen.Remove(key);
        }
    }

    public void TouchAdapterOnline(string sourceType, string sourceInstance, DateTimeOffset at)
    {
        lock (_stateGate)
        {
            var key = SourceKey(sourceType, sourceInstance);
            var grant = _store.GetGrant(sourceType, sourceInstance);
            if (grant is not null) _adapterLastSeen[key] = (at, grant.Credential);
        }
    }

    public void TouchAdapterOnline(RegistrationGrant grant, DateTimeOffset at)
    {
        lock (_stateGate)
        {
            var current = _store.GetGrant(grant.SourceType, grant.SourceInstance);
            if (current?.Credential == grant.Credential)
                _adapterLastSeen[SourceKey(grant.SourceType, grant.SourceInstance)] = (at, grant.Credential);
        }
    }

    public void SetAllowedTargets(string sourceType, IEnumerable<string> targetIds)
    {
        var grant = _registration.GetGrant(sourceType);
        if (grant is not null) _store.UpdatePermissions(grant.SourceType, grant.SourceInstance, targetIds, grant.Enabled);
    }

    public void ConfigureAllowedTargets(string credential, string sourceType, IEnumerable<string> targetIds, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        if (!string.Equals(grant.SourceType, sourceType, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The allowlist source identity does not match the registered adapter.");

        _store.UpdatePermissions(grant.SourceType, grant.SourceInstance, targetIds, grant.Enabled);
    }

    public void ConfigureAllowedTargets(string sourceType, string sourceInstance, IEnumerable<string> targetIds, bool enabled)
    {
        _store.UpdatePermissions(sourceType, sourceInstance, targetIds, enabled);
    }

    public void ConfigureSourceEnabled(string credential, string sourceType, bool enabled, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        if (!string.Equals(grant.SourceType, sourceType, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The source switch identity does not match the registered adapter.");

        _store.UpdatePermissions(grant.SourceType, grant.SourceInstance, grant.Targets, enabled);
    }

    public void ConfigureSourceEnabled(string sourceType, string sourceInstance, bool enabled)
    {
        var grant = _registration.GetGrant(sourceType, sourceInstance)
            ?? throw new UnauthorizedAccessException("The source is not registered.");
        _store.UpdatePermissions(sourceType, sourceInstance, grant.Targets, enabled);
    }

    public void SetConnectionEnabled(bool enabled) => _store.SetAcceptEvents(enabled);

    public void ClearPending() => _store.ClearPending();

    public RelayRouteReceipt RouteAdapterEvent(string credential, ProtocolEnvelope envelope, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        return RouteAdapterEvent(grant, envelope, at);
    }

    public RelayRouteReceipt RouteAdapterEvent(RegistrationGrant grant, ProtocolEnvelope envelope, DateTimeOffset at)
    {
        lock (_stateGate)
        {
        ArgumentNullException.ThrowIfNull(grant);
        grant = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
        if (!string.Equals(envelope.MessageType, "agent_event", StringComparison.Ordinal))
            throw new AgentProtocolValidationException("Adapter pipe accepts agent_event messages only.");

        AgentProtocolValidator.Validate(envelope);
        var eventMessage = AgentPayloadSanitizer.Sanitize(envelope.DeserializePayload<AgentEventMessage>());
        if (!string.Equals(eventMessage.SourceType, grant.SourceType, StringComparison.Ordinal)
            || !string.Equals(eventMessage.SourceInstance, grant.SourceInstance, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The event source identity does not match the registered adapter.");

        TouchAdapterOnline(grant.SourceType, grant.SourceInstance, at);
        if (!_store.AcceptEvents || !IsSourceEnabled(grant))
            return new RelayRouteReceipt(RelayRouteResult.Disabled);
        var sanitizedEnvelope = envelope with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(eventMessage, ProtocolEnvelope.JsonOptions) };
        AgentProtocolValidator.Validate(sanitizedEnvelope);
        if (System.Text.Encoding.UTF8.GetByteCount(sanitizedEnvelope.ToJson()) > FgoPet.AgentRuntime.Pipes.JsonLinePipeClient.MaxFrameBytes - 4096)
            throw new InvalidDataException("event_too_large");
        var queued = _store.EnqueueInbound(sanitizedEnvelope, at);
        return new RelayRouteReceipt(queued ? RelayRouteResult.Queued : RelayRouteResult.AlreadyApplied);
        }
    }

    public RelayRouteReceipt RouteDispatch(string credential, DispatchTaskRequest request, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        return RouteDispatch(grant, request, at, requireSourceIdentity: false);
    }

    public RelayRouteReceipt RouteDispatch(string sourceType, string sourceInstance, DispatchTaskRequest request, DateTimeOffset at)
    {
        var grant = _registration.GetGrant(sourceType, sourceInstance)
            ?? throw new UnauthorizedAccessException("The source is not registered.");
        return RouteDispatch(grant, request, at, requireSourceIdentity: true);
    }

    public RelayRouteReceipt RouteDispatch(RegistrationGrant grant, DispatchTaskRequest request, DateTimeOffset at, bool requireSourceIdentity = true)
    {
        lock (_stateGate)
        {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        grant = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
        if (requireSourceIdentity
            && (!string.Equals(request.SourceType, grant.SourceType, StringComparison.Ordinal)
                || !string.Equals(request.SourceInstanceId, grant.SourceInstance, StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("The dispatch source identity is missing or does not match the registered source.");

        var envelope = ProtocolEnvelope.Create("dispatch-" + request.DispatchRequestId, "dispatch_task", request);
        AgentProtocolValidator.Validate(envelope);
        if (System.Text.Encoding.UTF8.GetByteCount(envelope.ToJson()) > FgoPet.AgentRuntime.Pipes.JsonLinePipeClient.MaxFrameBytes - 4096)
            throw new InvalidDataException("dispatch_too_large");
        if (!_store.AcceptEvents || !IsSourceEnabled(grant))
            return new RelayRouteReceipt(RelayRouteResult.Disabled, request.DispatchRequestId);
        var existing = _store.GetDispatchReceipt(request.DispatchRequestId);
        if (existing is not null)
        {
            var digest = RelayStore.DispatchDigest(request);
            if (existing.SourceType.Length > 0 && !string.Equals(existing.SourceType, grant.SourceType, StringComparison.Ordinal)
                || existing.SourceInstance.Length > 0 && !string.Equals(existing.SourceInstance, grant.SourceInstance, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The dispatch receipt identity does not match the registered source.");
            if (existing.RequestDigest.Length > 0 && !string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The dispatch request ID is already bound to a different request.");
            if (string.Equals(existing.Result, "revoked", StringComparison.Ordinal))
                return new RelayRouteReceipt(RelayRouteResult.Unauthorized, request.DispatchRequestId, "dispatch_revoked");
            return new RelayRouteReceipt(RelayRouteResult.AlreadyApplied, request.DispatchRequestId);
        }

        if (!IsAdapterOnline(grant.SourceType, grant.SourceInstance, at))
            return new RelayRouteReceipt(RelayRouteResult.Offline, request.DispatchRequestId, "adapter_offline");

        if (!IsTargetAllowed(grant, request.TargetId))
            return new RelayRouteReceipt(RelayRouteResult.Unauthorized, request.DispatchRequestId, "target_not_allowed");

        try
        {
            if (!_store.TryEnqueueOutbound(
                    new QueuedDispatch(grant.SourceType, grant.SourceInstance, request, at),
                    out _))
                return new RelayRouteReceipt(RelayRouteResult.AlreadyApplied, request.DispatchRequestId);
        }
        catch (InvalidDataException error) when (error.Message is "relay_outbound_queue_full" or "relay_dispatch_receipts_full")
        {
            return new RelayRouteReceipt(RelayRouteResult.Backpressure, request.DispatchRequestId, "relay_backpressure");
        }
        return new RelayRouteReceipt(RelayRouteResult.Accepted, request.DispatchRequestId, TaskId: request.DispatchRequestId, SourceInstance: grant.SourceInstance);
        }
    }

    public IReadOnlyList<QueuedDispatch> DrainOutbound(string credential, DateTimeOffset at)
    {
        lock (_stateGate)
        {
        var grant = _registration.Authenticate(credential, at);
        TouchAdapterOnline(grant.SourceType, grant.SourceInstance, at);
        return _store.DrainOutbound(grant.SourceType, grant.SourceInstance);
        }
    }

    public IReadOnlyList<QueuedDispatch> DrainOutbound(RegistrationGrant grant, DateTimeOffset at, int maxBytes = int.MaxValue, bool consume = true)
    {
        lock (_stateGate)
        {
        var current = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
        TouchAdapterOnline(current.SourceType, current.SourceInstance, at);
        return _store.DrainOutbound(current.SourceType, current.SourceInstance, maxBytes, consume);
        }
    }

    public bool IsDispatchAllowed(RegistrationGrant grant, string targetId, DateTimeOffset at)
    {
        lock (_stateGate)
        {
            var current = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
            return _store.AcceptEvents && current.Enabled && current.Targets.Contains(targetId, StringComparer.Ordinal);
        }
    }

    public bool AnyAdapterOnline(DateTimeOffset at) =>
        _registration.ListSources(at).Any(source => IsAdapterOnline(source.SourceType, source.SourceInstanceId, at));

    public bool IsAdapterOnlineFor(string sourceType, string sourceInstance, DateTimeOffset at) =>
        IsAdapterOnline(sourceType, sourceInstance, at);

    public RelayOpenReceipt RouteOpen(string credential, OpenTaskRequest request, DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        return RouteOpen(grant, request, at);
    }

    public RelayOpenReceipt RouteOpen(RegistrationGrant grant, OpenTaskRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(grant);
        grant = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
        if (!string.Equals(request.SourceType, grant.SourceType, StringComparison.Ordinal)
            || !string.Equals(request.SourceInstance, grant.SourceInstance, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The open-task source identity does not match the registered adapter.");

        return IsAdapterOnline(grant.SourceType, grant.SourceInstance, at)
            ? new RelayOpenReceipt(AgentOpenTaskStatus.AppOnly, "exact_navigation_not_supported")
            : new RelayOpenReceipt(AgentOpenTaskStatus.Offline, "adapter_offline");
    }

    public IReadOnlyList<ProtocolEnvelope> DrainInbound(int maxBytes = int.MaxValue, bool consume = true)
    {
        // Draining is an App operation but is not itself a liveness heartbeat.
        // AppOnline is updated only by an explicit status heartbeat (or the
        // legacy test hook SetAppOnline), so a one-off drain cannot pin it online.
        return _store.DrainInbound(maxBytes, consume);
    }

    public string AcknowledgeInbound(EventAcknowledgementRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateGate)
        {
            TouchAppOnline(at);
            return _store.AcknowledgeInbound(request.SourceType, request.SourceInstanceId,
                request.EventKeys.Select(item => (item.TaskId, item.Sequence)));
        }
    }

    public AgentMaintenanceStatusResponse GetMaintenanceStatus(DateTimeOffset at)
    {
        lock (_stateGate)
        {
            var snapshot = _store.Snapshot;
            var counters = new List<AgentCapacityCounter>
            {
                new("relay_dispatch_receipts", snapshot.DispatchReceipts.Count, RelayStore.MaxDispatchReceipts,
                    snapshot.DispatchReceipts.Count(item => item.Acknowledged)),
                new("relay_event_watermarks", snapshot.InboundEventWatermarks.Count, RelayStore.MaxInboundWatermarks, 0),
                new("relay_inbound_queue", snapshot.Inbound.Count, RelayStore.MaxQueuedInboundEvents, 0),
                new("relay_outbound_queue", snapshot.Outbound.Count, RelayStore.MaxQueuedDispatches, 0),
                new("relay_archive_tombstones", snapshot.ArchiveTombstones.Count, RelayStore.MaxArchiveTombstones, 0),
            };
            var latestAdapter = _store.ListAdapterCapacityReports().FirstOrDefault();
            if (latestAdapter is not null)
            {
                counters.Add(latestAdapter.Counter with { Name = "adapter_journal" });
            }

            var activeBatch = _store.ListArchiveBatches().FirstOrDefault(batch =>
                batch.Phase is not (RelayArchiveBatchPhase.Completed or RelayArchiveBatchPhase.Rejected));
            var oldestArchivableAt = activeBatch?.Items.Min(item => item.EndedAt);
            return new AgentMaintenanceStatusResponse(counters, oldestArchivableAt, activeBatch?.BatchId, null);
        }
    }

    public RelayMaintenanceResponse PrepareArchive(AgentArchivePrepareRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateGate)
        {
            try
            {
                var batch = _store.PrepareArchive(request, at);
                return batch.Phase switch
                {
                    RelayArchiveBatchPhase.AwaitingAdapterPrepare => new RelayMaintenanceResponse(
                        "accepted", batch.BatchId, batch.Items, batch.BatchSha256,
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                    RelayArchiveBatchPhase.Prepared => new RelayMaintenanceResponse(
                        "already_prepared", batch.BatchId, null, batch.BatchSha256,
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                    RelayArchiveBatchPhase.AwaitingAdapterCommit => new RelayMaintenanceResponse(
                        "already_prepared", batch.BatchId, null, batch.BatchSha256,
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                    RelayArchiveBatchPhase.Completed => new RelayMaintenanceResponse(
                        "ok", batch.BatchId, null, batch.BatchSha256,
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                    _ => new RelayMaintenanceResponse(
                        "rejected", batch.BatchId, null, batch.BatchSha256, SafeError: batch.SafeError ?? "archive_rejected",
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                };
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            {
                return new RelayMaintenanceResponse("rejected", request.BatchId, null, request.BatchSha256,
                    SafeError: SafeError(error.Message));
            }
        }
    }

    public RelayMaintenanceResponse CommitArchive(AgentArchiveCommitRequest request, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateGate)
        {
            try
            {
                var batch = _store.CommitArchive(request.BatchId, request.BatchSha256, at);
                return batch.Phase switch
                {
                    RelayArchiveBatchPhase.AwaitingAdapterCommit => new RelayMaintenanceResponse(
                        "accepted", batch.BatchId, null, batch.BatchSha256,
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                    RelayArchiveBatchPhase.Completed => new RelayMaintenanceResponse(
                        "already_committed", batch.BatchId, null, batch.BatchSha256,
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                    _ => new RelayMaintenanceResponse(
                        "rejected", batch.BatchId, null, batch.BatchSha256, SafeError: batch.SafeError ?? "archive_rejected",
                        SourceType: batch.SourceType, SourceInstance: batch.SourceInstance),
                };
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            {
                return new RelayMaintenanceResponse("rejected", request.BatchId, null, request.BatchSha256,
                    SafeError: SafeError(error.Message));
            }
        }
    }

    public RelayMaintenanceResponse SyncMaintenance(
        string credential,
        AdapterMaintenanceSyncRequest request,
        DateTimeOffset at)
    {
        var grant = _registration.Authenticate(credential, at);
        return SyncMaintenance(grant, request, at);
    }

    public RelayMaintenanceResponse SyncMaintenance(
        RegistrationGrant grant,
        AdapterMaintenanceSyncRequest request,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateGate)
        {
            var current = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
            if (!string.Equals(request.SourceType, current.SourceType, StringComparison.Ordinal)
                || !string.Equals(request.SourceInstance, current.SourceInstance, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The maintenance source identity does not match the authenticated adapter.");

            TouchAdapterOnline(current.SourceType, current.SourceInstance, at);
            var acknowledgement = _store.AcknowledgeArchive(request, at);
            if (acknowledgement.Result != "none")
            {
                return new RelayMaintenanceResponse(
                    acknowledgement.Result,
                    acknowledgement.Batch?.BatchId ?? request.AcknowledgedBatchId,
                    BatchSha256: acknowledgement.Batch?.BatchSha256,
                    AcknowledgedBatchId: request.AcknowledgedBatchId,
                    AcknowledgedPhase: request.AcknowledgedPhase,
                    SafeError: acknowledgement.SafeError,
                    SourceType: current.SourceType,
                    SourceInstance: current.SourceInstance);
            }

            var batch = _store.ListArchiveBatches().FirstOrDefault(item =>
                item.SourceType == current.SourceType
                && item.SourceInstance == current.SourceInstance
                && (item.Phase is RelayArchiveBatchPhase.AwaitingAdapterPrepare
                    or RelayArchiveBatchPhase.AwaitingAdapterCommit));
            if (batch is null)
                return new RelayMaintenanceResponse("none");

            return batch.Phase == RelayArchiveBatchPhase.AwaitingAdapterPrepare
                ? new RelayMaintenanceResponse("prepare", batch.BatchId, batch.Items, batch.BatchSha256,
                    SourceType: batch.SourceType, SourceInstance: batch.SourceInstance)
                : new RelayMaintenanceResponse("commit", batch.BatchId, null, batch.BatchSha256,
                    SourceType: batch.SourceType, SourceInstance: batch.SourceInstance);
        }
    }

    public string AcknowledgeDispatches(
        RegistrationGrant grant,
        DispatchAcknowledgementRequest request,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(request);
        lock (_stateGate)
        {
            var current = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
            if (!string.Equals(request.SourceType, current.SourceType, StringComparison.Ordinal)
                || !string.Equals(request.SourceInstanceId, current.SourceInstance, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The dispatch acknowledgement identity does not match the registered adapter.");
            return _store.AcknowledgeOutbound(current.SourceType, current.SourceInstance, request.DispatchRequestIds);
        }
    }

    internal void CompleteSentBatch(string responseJson)
    {
        // Delivery is intentionally completed only by explicit application/adapter
        // acknowledgements after their durable side effects have succeeded.
    }

    private bool IsSourceEnabled(RegistrationGrant grant) => grant.Enabled;

    private bool IsAdapterOnline(string sourceType, string sourceInstance, DateTimeOffset at)
    {
        lock (_stateGate)
        {
            var key = SourceKey(sourceType, sourceInstance);
            if (!_adapterLastSeen.TryGetValue(key, out var lastSeen)) return false;
            var grant = _store.GetGrant(sourceType, sourceInstance);
            return grant is not null && grant.Credential == lastSeen.Credential && at >= lastSeen.At && at - lastSeen.At <= AdapterLease;
        }
    }

    private static bool IsTargetAllowed(RegistrationGrant grant, string targetId) => grant.Targets.Contains(targetId, StringComparer.Ordinal);

    private static string SafeError(string value)
    {
        var compact = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return compact.Length <= 512 ? compact : compact[..512];
    }

    private static string SourceKey(string sourceType, string sourceInstance) => $"{sourceType}\u001f{sourceInstance}";
}
