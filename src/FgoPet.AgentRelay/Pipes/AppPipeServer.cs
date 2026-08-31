using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.AgentRelay.Pipes;

public sealed class AppPipeServer
{
    private readonly RelayRouter _router;
    private readonly RegistrationService _registration;
    private readonly string _pipeName;
    private readonly TimeProvider _time;
    private readonly TimeSpan _operationTimeout;

    public AppPipeServer(
        RelayRouter router,
        string pipeName,
        RegistrationService registration,
        TimeProvider? timeProvider = null,
        TimeSpan? operationTimeout = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _time = timeProvider ?? TimeProvider.System;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public Task<string> ProcessLineAsync(string line) => ProcessLineCoreAsync(line, _time.GetUtcNow());

    public Task RunAsync(CancellationToken cancellationToken) => RunCoreAsync(cancellationToken, null);

    internal Task RunAsync(CancellationToken cancellationToken, NamedPipeServerStream initialListener) =>
        RunCoreAsync(cancellationToken, initialListener ?? throw new ArgumentNullException(nameof(initialListener)));

    private async Task RunCoreAsync(CancellationToken cancellationToken, NamedPipeServerStream? initialListener)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            initialListener?.Dispose();
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = initialListener ?? CreateListener();
            initialListener = null;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidDataException or System.Security.Cryptography.CryptographicException or DecoderFallbackException) { }
            catch (IOException)
            {
                // Peer failures are isolated to the accepted connection.
            }
            catch (UnauthorizedAccessException)
            {
                // An ACL/authentication failure cannot damage the listener loop.
            }
        }
    }

    internal NamedPipeServerStream CreateListener() => new(
        _pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task HandleConnectionAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var reader = new JsonLineFrameReader(pipe);
        var writer = new PipeResponseWriter(pipe, _operationTimeout);
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_operationTimeout);
                line = await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (DecoderFallbackException)
            {
                return;
            }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
            {
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (line is null) return;
            ProtocolEnvelope envelope;
            try
            {
                envelope = ProtocolEnvelope.Parse(line);
            }
            catch (Exception error) when (error is AgentProtocolValidationException or JsonException)
            {
                await WriteAsync(writer, Error(Guid.NewGuid().ToString("N"), "invalid_request", "invalid_request"), cancellationToken).ConfigureAwait(false);
                return;
            }
            try { AgentProtocolValidator.Validate(envelope); }
            catch (AgentProtocolValidationException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "invalid_request", "invalid_request"), cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var response = await ProcessLineCoreAsync(line, _time.GetUtcNow(), consume: false).ConfigureAwait(false);
                await writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                _router.CompleteSentBatch(response);
            }
            catch (AgentProtocolValidationException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "invalid_request", "invalid_request"), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "unauthorized", "source_unauthorized"), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidDataException or System.Security.Cryptography.CryptographicException or IOException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "state_or_payload_unavailable", "state_or_payload_unavailable"), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (JsonException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "invalid_request", "invalid_request"), cancellationToken).ConfigureAwait(false);
            }
            catch (FormatException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "invalid_request", "invalid_request"), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await WriteAsync(writer, Error(envelope.MessageId, "invalid_request", "operation_rejected"), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task<string> ProcessLineCoreAsync(string line, DateTimeOffset at, bool consume = true)
    {
        var envelope = ProtocolEnvelope.Parse(line);
        AgentProtocolValidator.Validate(envelope);
        var messageId = envelope.MessageId;

        switch (envelope.MessageType)
        {
            case "connection_test":
                return Task.FromResult(Response(messageId, "connection_test", new RelayConnectionTestResponse(
                    RelayOnline: true,
                    AppOnline: _router.IsAppOnline(at),
                    AdapterOnline: _router.AnyAdapterOnline(at),
                    ProtocolVersion: ProtocolEnvelope.CurrentProtocolVersion,
                    Status: !_router.IsAppOnline(at) ? "app_offline" : !_router.AnyAdapterOnline(at) ? "adapter_offline" : "connected",
                    ObservedAtUtc: at,
                    Error: null)).ToJson());

            case "pending_sources":
                return Task.FromResult(Response(messageId, "pending_sources", new
                {
                    result = "pending_sources",
                    sources = _registration.ListPendingSources(at),
                }).ToJson());

            case "decide_registration":
            {
                var request = envelope.DeserializePayload<RegistrationDecisionRequest>();
                if (string.Equals(request.Decision, "approve", StringComparison.Ordinal))
                    _registration.Approve(request.RequestId, at);
                else if (string.Equals(request.Decision, "reject", StringComparison.Ordinal))
                    _registration.Reject(request.RequestId, at);
                else
                    throw new AgentProtocolValidationException("Unknown registration decision.");
                return Task.FromResult(Response(messageId, "decide_registration", new { result = "ok" }).ToJson());
            }

            case "list_sources":
                return Task.FromResult(Response(messageId, "list_sources", new
                {
                    result = "list_sources",
                    sources = _registration.ListSources(at, grant => _router.IsAdapterOnlineFor(grant.SourceType, grant.SourceInstance, at)),
                }).ToJson());

            case "update_permissions":
            {
                var request = envelope.DeserializePayload<UpdatePermissionsRequest>();
                _router.ConfigureAllowedTargets(request.SourceType, request.SourceInstanceId, request.AllowedTargetIds, request.Enabled);
                return Task.FromResult(Response(messageId, "update_permissions", new { result = "ok" }).ToJson());
            }

            case "revoke_source":
            {
                var request = envelope.DeserializePayload<RevokeSourceRequest>();
                _registration.Revoke(request.SourceType, request.SourceInstanceId);
                return Task.FromResult(Response(messageId, "revoke_source", new { result = "ok" }).ToJson());
            }

            case "status_check":
                return Task.FromResult(ProcessStatusCheck(envelope, at, consume));

            case "open_task":
            {
                var request = envelope.DeserializePayload<OpenTaskRequest>();
                var grant = _registration.GetGrant(request.SourceType, request.SourceInstance)
                    ?? throw new UnauthorizedAccessException("The source is not registered.");
                var open = _router.RouteOpen(grant, request, at);
                return Task.FromResult(Response(messageId, "open_task", new
                {
                    result = open.Status.ToString().ToLowerInvariant(),
                    error = open.Error,
                }).ToJson());
            }

            case "dispatch_task":
            {
                var request = envelope.DeserializePayload<DispatchTaskRequest>();
                if (string.IsNullOrWhiteSpace(request.SourceType) || string.IsNullOrWhiteSpace(request.SourceInstanceId))
                    throw new UnauthorizedAccessException("Dispatch source identity is required.");
                var receipt = _router.RouteDispatch(request.SourceType, request.SourceInstanceId, request, at);
                return Task.FromResult(Response(messageId, "dispatch_task", new
                {
                    result = receipt.Result switch
                    {
                        RelayRouteResult.AlreadyApplied => "already_applied",
                        _ => receipt.Result.ToString().ToLowerInvariant(),
                    },
                    dispatch_request_id = request.DispatchRequestId,
                    task_id = receipt.TaskId,
                    source_instance = receipt.SourceInstance,
                }).ToJson());
            }

            case "authenticate":
                throw new UnauthorizedAccessException("The app-control pipe does not accept credentials.");

            default:
                return Task.FromResult(Error(messageId, "unsupported_operation", "unsupported_operation").ToJson());
        }
    }

    private string ProcessStatusCheck(ProtocolEnvelope envelope, DateTimeOffset at, bool consume)
    {

        if (envelope.Payload.TryGetProperty("clear_pending", out var clear) && clear.ValueKind == JsonValueKind.True)
            _router.ClearPending();
        if (envelope.Payload.TryGetProperty("enabled", out var enabled)
            && (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
            _router.SetConnectionEnabled(enabled.GetBoolean());

        var hasSourceType = envelope.Payload.TryGetProperty("source_type", out var sourceType);
        var hasSourceInstance = envelope.Payload.TryGetProperty("source_instance_id", out var sourceInstance);
        var hasTargets = envelope.Payload.TryGetProperty("allowed_targets", out var allowedTargets);
        var hasSourceEnabled = envelope.Payload.TryGetProperty("source_enabled", out _);
        if (hasSourceEnabled && !hasSourceType && !hasSourceInstance)
            throw new AgentProtocolValidationException("Source enabled state requires source type and source instance.");
        if (hasTargets)
        {
            if (!hasSourceType || sourceType.ValueKind != JsonValueKind.String
                || !hasSourceInstance || sourceInstance.ValueKind != JsonValueKind.String
                || !hasTargets || allowedTargets.ValueKind != JsonValueKind.Array)
                throw new AgentProtocolValidationException("Source type, source instance, and target IDs are required together.");
            var sourceTypeText = sourceType.GetString()!;
            var sourceInstanceText = sourceInstance.GetString()!;
            var current = _registration.GetGrant(sourceTypeText, sourceInstanceText)
                ?? throw new UnauthorizedAccessException("The source is not registered.");
            var sourceEnabledValue = current.Enabled;
            if (envelope.Payload.TryGetProperty("source_enabled", out var sourceEnabled)
                && (sourceEnabled.ValueKind == JsonValueKind.True || sourceEnabled.ValueKind == JsonValueKind.False))
                sourceEnabledValue = sourceEnabled.GetBoolean();
            _router.ConfigureAllowedTargets(sourceTypeText, sourceInstanceText, ParseTargetIds(allowedTargets), sourceEnabledValue);
        }
        else if (hasSourceType || hasSourceInstance)
        {
            if (!hasSourceType || sourceType.ValueKind != JsonValueKind.String
                || !hasSourceInstance || sourceInstance.ValueKind != JsonValueKind.String)
                throw new AgentProtocolValidationException("Source type and source instance are required together.");
            if (envelope.Payload.TryGetProperty("source_enabled", out var sourceEnabled)
                && (sourceEnabled.ValueKind == JsonValueKind.True || sourceEnabled.ValueKind == JsonValueKind.False))
                _router.ConfigureSourceEnabled(sourceType.GetString()!, sourceInstance.GetString()!, sourceEnabled.GetBoolean());
        }

        var includeEvents = envelope.Payload.TryGetProperty("include_events", out var include)
            && include.ValueKind == JsonValueKind.True;
        if (includeEvents) _router.TouchAppOnline(at);
        var pendingCount = _router.PendingInboundCount;
        var events = includeEvents ? _router.DrainInbound(JsonLinePipeClient.MaxFrameBytes - 4096, consume).ToArray() : Array.Empty<ProtocolEnvelope>();
        return Response(envelope.MessageId, "status_check", new
        {
            result = "status",
            connected = true,
            protocol_version = ProtocolEnvelope.CurrentProtocolVersion,
            pending_count = pendingCount,
            events,
        }).ToJson();
    }

    private static IReadOnlyList<string> ParseTargetIds(JsonElement values)
    {
        var result = values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null).ToArray();
        if (result.Any(string.IsNullOrWhiteSpace)) throw new AgentProtocolValidationException("Target IDs must be opaque strings.");
        return result.Select(value => value!).ToArray();
    }

    private static ProtocolEnvelope Response(string messageId, string messageType, object payload) =>
        ProtocolEnvelope.Create(messageId, messageType, payload);

    private static ProtocolEnvelope Error(string messageId, string result, string error) =>
        Response(messageId, "error", new { result, error });

    private static async Task WriteAsync(PipeResponseWriter writer, ProtocolEnvelope response, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
