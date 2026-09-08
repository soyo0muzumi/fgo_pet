using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRuntime;

namespace FgoPet.CodexAdapter.Relay;

public interface IAdapterRelayTransport
{
    Task<ProtocolEnvelope> SendAsync(ProtocolEnvelope request, AuthenticateRequest? authentication = null,
        CancellationToken cancellationToken = default);
}

public sealed class CodexRelayConnector : ICodexRelayConnector
{
    private readonly IAdapterIdentityStore _store;
    private readonly IAdapterRelayTransport _transport;
    private readonly Func<CancellationToken, Task<RelayBootstrapResult>> _ensureRelay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdapterIdentityState _identity;
    private bool _revoked;
    private bool _revocationPending;
    private bool _authenticated;

    public CodexRelayConnector(IAdapterIdentityStore store, IAdapterRelayTransport transport,
        Func<CancellationToken, Task<RelayBootstrapResult>> ensureRelay)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ensureRelay = ensureRelay ?? throw new ArgumentNullException(nameof(ensureRelay));
        _identity = _store.LoadOrCreate();
        SourceInstanceId = _identity.SourceInstanceId;
    }

    public string SourceInstanceId { get; }

    public async Task<AdapterConnectionResult> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await EnsureCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<AdapterConnectionResult> EnsureCoreAsync(CancellationToken cancellationToken)
    {
        if (_revoked) return new(AdapterConnectionStatus.Revoked);
        try
        {
            if (_revocationPending) return MarkRevoked();
            _identity = _store.LoadOrCreate();
            if (_identity.SourceInstanceId != SourceInstanceId)
                return new(AdapterConnectionStatus.Rejected, Error: "identity_changed_restart_required");

            var bootstrap = await _ensureRelay(cancellationToken).ConfigureAwait(false);
            if (bootstrap.Status != RelayBootstrapStatus.Ready)
                return new(bootstrap.Status == RelayBootstrapStatus.VersionMismatch
                    ? AdapterConnectionStatus.VersionMismatch : AdapterConnectionStatus.RelayOffline);

            if (_identity.Credential is null)
            {
                var request = _identity.RequestId is null
                    ? ProtocolEnvelope.Create(NewId(), "registration_request", new RegistrationRequestMessage(
                        "codex", "Codex", SourceInstanceId, "1", ProtocolEnvelope.CurrentProtocolVersion, _identity.RequestNonce))
                    : ProtocolEnvelope.Create(NewId(), "registration_status", new RegistrationStatusRequest(
                        _identity.RequestId, SourceInstanceId, _identity.RequestNonce));
                var response = await _transport.SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response.MessageType != "registration_status") return Failure(response);
                var status = response.DeserializePayload<RegistrationStatusResponse>();
                AgentProtocolValidator.ValidateResponse(response);
                if (status.SourceInstanceId != SourceInstanceId
                    || _identity.RequestId is not null && status.RequestId != _identity.RequestId)
                    return new(AdapterConnectionStatus.Rejected, Error: "registration_identity_mismatch");

                switch (status.Status)
                {
                    case "pending":
                        Persist(_identity with { RequestId = status.RequestId });
                        return new(AdapterConnectionStatus.ApprovalRequired, status.RequestId);
                    case "approved" when status.Credential is not null:
                        // A lost reply remains recoverable until authentication consumes delivery.
                        Persist(_identity with { RequestId = status.RequestId, Credential = status.Credential });
                        break;
                    case "expired":
                        Persist(_identity with { RequestId = null, RequestNonce = AdapterIdentityState.NewNonce() });
                        return new(AdapterConnectionStatus.ApprovalRequired);
                    case "revoked":
                        return MarkRevoked();
                    default:
                        return new(AdapterConnectionStatus.Rejected, status.RequestId, "registration_rejected");
                }
            }

            var authenticated = await _transport.SendAsync(
                ProtocolEnvelope.Create(NewId(), "authenticate", Authentication()), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (ReadResult(authenticated) != "authenticated") return Failure(authenticated);
            _authenticated = true;
            return new(AdapterConnectionStatus.Connected);
        }
        catch (AdapterConnectionException error)
        {
            _authenticated = false;
            return error.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _authenticated = false;
            return new(AdapterConnectionStatus.RelayOffline);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            _authenticated = false;
            return new(AdapterConnectionStatus.RelayOffline, Error: "connection_or_state_unavailable");
        }
    }

    public async Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.SourceType != "codex" || message.SourceInstance != SourceInstanceId)
            throw new ArgumentException("The event must belong to this adapter identity.", nameof(message));
        var envelope = ProtocolEnvelope.Create(
            $"event-{SourceInstanceId}-{message.TaskId}-{message.Sequence}", "agent_event", AgentPayloadSanitizer.Sanitize(message));
        AgentProtocolValidator.Validate(envelope);
        var response = await SendOperationAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (ReadResult(response) is not "queued" and not "alreadyapplied" and not "already_applied" and not "ok")
            throw new AdapterConnectionException(new(AdapterConnectionStatus.RelayOffline, Error: "event_rejected"));
    }

    public async Task<IReadOnlyList<DispatchTaskRequest>> PollDispatchesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendOperationAsync(ProtocolEnvelope.Create(NewId(), "status_check", new { include_dispatches = true }), cancellationToken)
            .ConfigureAwait(false);
        if (!response.Payload.TryGetProperty("dispatches", out var dispatches) || dispatches.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing dispatch collection.");
        var result = new List<DispatchTaskRequest>();
        foreach (var item in dispatches.EnumerateArray())
        {
            var envelope = ProtocolEnvelope.Parse(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
            AgentProtocolValidator.Validate(envelope);
            if (envelope.MessageType != "dispatch_task") throw new InvalidDataException("Unexpected dispatch type.");
            result.Add(envelope.DeserializePayload<DispatchTaskRequest>());
        }
        return result;
    }

    public async Task<string> AcknowledgeDispatchesAsync(IReadOnlyList<string> dispatchRequestIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatchRequestIds);
        if (dispatchRequestIds.Count == 0) return "already_acknowledged";

        var result = "already_acknowledged";
        foreach (var batch in dispatchRequestIds.Chunk(512))
        {
            var response = await SendOperationAsync(
                ProtocolEnvelope.Create(NewId(), "dispatch_ack", new DispatchAcknowledgementRequest(
                    "codex", SourceInstanceId, batch)), cancellationToken).ConfigureAwait(false);
            result = ReadResult(response) ?? throw new InvalidDataException("Missing dispatch acknowledgement result.");
            if (result is not "acknowledged" and not "already_acknowledged" and not "unknown")
                throw new InvalidDataException("Unknown dispatch acknowledgement result.");
        }
        return result;
    }

    public async Task<bool> IsDispatchAllowedAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var response = await SendOperationAsync(ProtocolEnvelope.Create(NewId(), "status_check", new { target_id = targetId }), cancellationToken).ConfigureAwait(false);
        return response.Payload.TryGetProperty("dispatch_allowed", out var allowed) && allowed.ValueKind == JsonValueKind.True;
    }

    public async Task<AdapterMaintenanceSyncResult> SyncMaintenanceAsync(
        string? acknowledgedBatchId,
        string? acknowledgedPhase,
        string? safeError,
        AgentCapacityCounter adapterJournal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapterJournal);
        var response = await SendOperationAsync(
            ProtocolEnvelope.Create(NewId(), "maintenance_sync", new AdapterMaintenanceSyncRequest(
                "codex", SourceInstanceId, acknowledgedBatchId, acknowledgedPhase, safeError, adapterJournal)),
            cancellationToken).ConfigureAwait(false);
        AgentProtocolValidator.ValidateResponse(response);
        var result = ReadResult(response) ?? throw new InvalidDataException("Missing maintenance sync result.");
        if (result == "none") return new AdapterMaintenanceSyncResult("none");

        if (result == "prepare")
        {
            var prepare = response.DeserializePayload<AgentArchivePrepareRequest>();
            return new AdapterMaintenanceSyncResult(
                result, prepare.BatchId, prepare.Items, prepare.BatchSha256);
        }

        if (result == "commit")
        {
            var commit = response.DeserializePayload<AgentArchiveCommitRequest>();
            return new AdapterMaintenanceSyncResult(result, commit.BatchId, BatchSha256: commit.BatchSha256);
        }

        if (result is "prepared" or "committed" or "rejected")
        {
            return new AdapterMaintenanceSyncResult(
                result,
                AcknowledgedBatchId: ReadOptionalString(response, "acknowledged_batch_id"),
                AcknowledgedPhase: ReadOptionalString(response, "acknowledged_phase"),
                SafeError: ReadOptionalString(response, "safe_error"));
        }

        throw new InvalidDataException("Unknown maintenance sync result.");
    }

    private async Task<ProtocolEnvelope> SendOperationAsync(ProtocolEnvelope request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_authenticated || _identity.Credential is null || _revoked)
            {
                var connection = await EnsureCoreAsync(cancellationToken).ConfigureAwait(false);
                if (connection.Status != AdapterConnectionStatus.Connected) throw new AdapterConnectionException(connection);
            }
            var response = await _transport.SendAsync(request, Authentication(), cancellationToken).ConfigureAwait(false);
            if (ReadResult(response) is "revoked" or "unauthorized" || response.MessageType == "error")
                throw new AdapterConnectionException(Failure(response));
            return response;
        }
        catch (AdapterConnectionException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _authenticated = false;
            throw new AdapterConnectionException(new(AdapterConnectionStatus.RelayOffline));
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            _authenticated = false;
            throw new AdapterConnectionException(new(AdapterConnectionStatus.RelayOffline));
        }
        finally { _gate.Release(); }
    }

    private AdapterConnectionResult Failure(ProtocolEnvelope response)
    {
        _authenticated = false;
        return ReadResult(response) switch
        {
            "revoked" => MarkRevoked(),
            "version_mismatch" => new(AdapterConnectionStatus.VersionMismatch),
            "unauthorized" => new(AdapterConnectionStatus.Rejected, Error: "authentication_failed"),
            _ => new(AdapterConnectionStatus.RelayOffline, Error: "relay_rejected"),
        };
    }

    private AdapterConnectionResult MarkRevoked()
    {
        _authenticated = false;
        _revocationPending = true;
        var current = _store.LoadOrCreate();
        // A sibling MCP/hook may already have cleared this credential or paired again.
        // Never overwrite its newer credential while cleaning up the revoked one.
        if (current.SourceInstanceId == SourceInstanceId && current.Credential == _identity.Credential)
        {
            _identity = current;
            Persist(_identity with { Credential = null, RequestId = null, RequestNonce = AdapterIdentityState.NewNonce() });
        }
        _revoked = true;
        _revocationPending = false;
        return new(AdapterConnectionStatus.Revoked);
    }

    private void Persist(AdapterIdentityState updated)
    {
        if (!_store.TrySave(_identity, updated)) throw new IOException("Adapter state changed concurrently; retry with the current state.");
        _identity = updated;
    }

    private AuthenticateRequest Authentication() => new("codex", SourceInstanceId,
        _identity.Credential ?? throw new InvalidOperationException("The adapter has no credential."));

    private static string? ReadResult(ProtocolEnvelope response) => response.Payload.ValueKind == JsonValueKind.Object
        && response.Payload.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static string? ReadOptionalString(ProtocolEnvelope response, string property) =>
        response.Payload.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static bool IsRecoverable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException
        or JsonException or AgentProtocolValidationException or DecoderFallbackException or TimeoutException;
}
