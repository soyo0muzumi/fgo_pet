using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay.Registration;
using FgoPet.AgentRelay.Routing;
using FgoPet.AgentRelay.Storage;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.AgentRelay.Pipes;

public sealed class AdapterPipeServer
{
    private readonly RelayRouter _router;
    private readonly RegistrationService _registration;
    private readonly string _pipeName;
    private readonly TimeProvider _time;
    private readonly TimeSpan _operationTimeout;

    public AdapterPipeServer(
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

    /// <summary>Processes one already-authenticated request for focused unit tests.</summary>
    public Task<string> ProcessLineAsync(string line, RegistrationGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return ProcessAuthenticatedLineAsync(line, grant, _time.GetUtcNow());
    }

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
                // A malformed/disconnected peer must only end this connection.
            }
            catch (UnauthorizedAccessException)
            {
                // Current-user ACL or an authentication failure is connection scoped.
            }
            catch (Exception error) when (error is AgentProtocolValidationException or JsonException or InvalidOperationException or FormatException)
            {
                // A malformed payload must not fault the long-lived listener task.
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
        RegistrationGrant? grant = null;

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
                await WriteErrorAsync(writer, null, "invalid_request", cancellationToken).ConfigureAwait(false);
                return;
            }
            try { AgentProtocolValidator.Validate(envelope); }
            catch (AgentProtocolValidationException)
            {
                await WriteErrorAsync(writer, envelope.MessageId, "invalid_request", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (grant is null)
            {
                if (string.Equals(envelope.MessageType, "registration_request", StringComparison.Ordinal))
                {
                    var request = envelope.DeserializePayload<RegistrationRequestMessage>();
                    // The production pipe requires the new identity/nonce schema, never the legacy prototype.
                    AgentProtocolValidator.Validate(ProtocolEnvelope.Create(envelope.MessageId, "registration_request", request));
                    PendingRegistration pending;
                    try { pending = _registration.Request(request, _time.GetUtcNow()); }
                    catch (Exception error) when (error is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
                    {
                        await WriteErrorAsync(writer, envelope.MessageId, "registration_unavailable", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    var response = _registration.Poll(
                        new RegistrationStatusRequest(pending.RequestId, pending.SourceInstance, pending.RequestNonce),
                        _time.GetUtcNow());
                    await WriteAsync(writer, Response(envelope.MessageId, "registration_status", response), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.Equals(envelope.MessageType, "registration_status", StringComparison.Ordinal))
                {
                    var request = envelope.DeserializePayload<RegistrationStatusRequest>();
                    var response = _registration.Poll(request, _time.GetUtcNow());
                    await WriteAsync(writer, Response(envelope.MessageId, "registration_status", response), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(envelope.MessageType, "authenticate", StringComparison.Ordinal))
                {
                    await WriteErrorAsync(writer, envelope.MessageId, "unauthorized", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var authRequest = envelope.DeserializePayload<AuthenticateRequest>();
                try
                {
                    grant = _registration.Authenticate(authRequest.SourceType, authRequest.SourceInstanceId, authRequest.Credential, _time.GetUtcNow());
                    _router.TouchAdapterOnline(grant, _time.GetUtcNow());
                    await WriteAsync(writer, Response(envelope.MessageId, "authenticate", new { result = "authenticated" }), cancellationToken).ConfigureAwait(false);
                }
                catch (RevokedRegistrationException)
                {
                    await WriteAsync(writer, Response(envelope.MessageId, "authenticate", new { result = "revoked" }), cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    await WriteAsync(writer, Response(envelope.MessageId, "authenticate", new { result = "unauthorized" }), cancellationToken).ConfigureAwait(false);
                    return;
                }

                continue;
            }

            try
            {
                // Re-read the grant on every operation so a revoke applies to existing connections.
                var current = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, _time.GetUtcNow());
                var response = await ProcessAuthenticatedLineAsync(line, current, _time.GetUtcNow(), consume: false).ConfigureAwait(false);
                await writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                _router.CompleteSentBatch(response);
            }
            catch (RevokedRegistrationException)
            {
                await WriteAsync(writer, Response(envelope.MessageId, "error", new { result = "revoked", error = "source_revoked" }), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                await WriteAsync(writer, Response(envelope.MessageId, "error", new { result = "unauthorized", error = "source_unauthorized" }), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is InvalidDataException or System.Security.Cryptography.CryptographicException or IOException)
            {
                await WriteErrorAsync(writer, envelope.MessageId, "state_or_payload_unavailable", cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (AgentProtocolValidationException)
            {
                await WriteAsync(writer, Response(envelope.MessageId, "error", new { result = "invalid_request", error = "invalid_request" }), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task<string> ProcessAuthenticatedLineAsync(string line, RegistrationGrant grant, DateTimeOffset at, bool consume = true)
    {
        var envelope = ProtocolEnvelope.Parse(line);
        AgentProtocolValidator.Validate(envelope);
        grant = _registration.Authenticate(grant.SourceType, grant.SourceInstance, grant.Credential, at);
        if (envelope.MessageType == "connection_test")
        {
            _router.TouchAdapterOnline(grant, at);
            var appOnline = _router.IsAppOnline(at);
            return Task.FromResult(Response(envelope.MessageId, "connection_test", new RelayConnectionTestResponse(
                true, appOnline, true, ProtocolEnvelope.CurrentProtocolVersion,
                appOnline ? "connected" : "app_offline", at, null)).ToJson());
        }
        if (string.Equals(envelope.MessageType, "status_check", StringComparison.Ordinal))
        {
            var includeDispatches = envelope.Payload.TryGetProperty("include_dispatches", out var value)
                && value.ValueKind == JsonValueKind.True;
            var dispatches = includeDispatches
                ? _router.DrainOutbound(grant, at, JsonLinePipeClient.MaxFrameBytes - 4096, consume)
                    .Select(item => ProtocolEnvelope.Create(
                        "dispatch-" + item.Request.DispatchRequestId,
                        "dispatch_task",
                        item.Request,
                        item.EnqueuedAt))
                    .ToArray()
                : Array.Empty<ProtocolEnvelope>();
            var allowed = envelope.Payload.TryGetProperty("target_id", out var target) && target.ValueKind == JsonValueKind.String
                && _router.IsDispatchAllowed(grant, target.GetString()!, at);
            return Task.FromResult(Response(envelope.MessageId, "status_check", new { result = "status", dispatches, dispatch_allowed = allowed }).ToJson());
        }

        if (!string.Equals(envelope.MessageType, "agent_event", StringComparison.Ordinal))
            throw new AgentProtocolValidationException("Adapter pipe accepts agent_event and status_check messages only.");

        var receipt = _router.RouteAdapterEvent(grant, envelope, at);
        return Task.FromResult(Response(envelope.MessageId, "agent_event", new
        {
            result = receipt.Result switch
            {
                RelayRouteResult.AlreadyApplied => "already_applied",
                _ => receipt.Result.ToString().ToLowerInvariant(),
            },
        }).ToJson());
    }

    private static ProtocolEnvelope Response(string messageId, string messageType, object payload) =>
        ProtocolEnvelope.Create(messageId, messageType, payload);

    private static async Task WriteAsync(PipeResponseWriter writer, ProtocolEnvelope response, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(PipeResponseWriter writer, string? messageId, string error, CancellationToken cancellationToken) =>
        WriteAsync(writer, Response(messageId ?? Guid.NewGuid().ToString("N"), "error", new { result = error, error }), cancellationToken);
}
