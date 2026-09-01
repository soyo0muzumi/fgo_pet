using System.Text.Json;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;

namespace FgoPet.AgentProtocol.Validation;

public static class AgentProtocolValidator
{
    private static readonly HashSet<string> DenylistedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "command", "prompt", "reasoning", "tool_call", "tool_arguments", "terminal",
        "environment", "credential", "credentials", "window", "document", "cwd",
        "working_directory", "path", "local_path", "transcript", "stdout", "stderr",
    };

    private static readonly HashSet<string> MaintenanceDenylistedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "summary", "title", "description", "user_text",
    };

    private static readonly HashSet<string> KnownMessageTypes = new(StringComparer.Ordinal)
    {
        "agent_event", "dispatch_task", "open_task", "registration_request",
        "registration_approval", "registration_response", "registration_status",
        "authenticate", "connection_test", "pending_sources", "decide_registration",
        "list_sources", "update_permissions", "revoke_source", "status_check", "error",
        "event_ack", "dispatch_ack", "maintenance_status", "archive_prepare", "archive_commit",
        "maintenance_sync",
    };

    private static readonly HashSet<string> KnownRegistrationStatuses = new(StringComparer.Ordinal)
    {
        "pending", "approved", "rejected", "expired", "unauthorized", "revoked",
    };

    private static readonly HashSet<string> KnownConnectionStatuses = new(StringComparer.Ordinal)
    {
        "connected", "degraded", "offline", "relay_offline", "adapter_offline", "app_offline",
        "authentication_failed", "version_mismatch", "awaiting_approval", "disabled", "error",
    };

    private static readonly HashSet<string> KnownRegistrationDecisions = new(StringComparer.Ordinal)
    {
        "approve", "reject",
    };

    private static readonly HashSet<string> KnownTerminalStatuses = new(StringComparer.Ordinal)
    {
        "completed", "failed", "cancelled",
    };

    private static readonly HashSet<string> KnownAcknowledgementPhases = new(StringComparer.Ordinal)
    {
        "prepare", "commit",
    };

    /// <summary>
    /// Validates an envelope sent as a request. Response payloads must be
    /// validated through <see cref="ValidateResponse"/> so direction-specific
    /// fields cannot be smuggled into the other schema.
    /// </summary>
    public static void Validate(ProtocolEnvelope envelope) => ValidateEnvelope(envelope, isResponse: false);

    /// <summary>Validates an envelope received as a response.</summary>
    public static void ValidateResponse(ProtocolEnvelope envelope) => ValidateEnvelope(envelope, isResponse: true);

    private static void ValidateEnvelope(ProtocolEnvelope envelope, bool isResponse)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.ProtocolVersion, ProtocolEnvelope.CurrentProtocolVersion, StringComparison.Ordinal))
        {
            throw new AgentProtocolValidationException($"Unsupported protocol version '{envelope.ProtocolVersion}'.");
        }

        RequireText(envelope.MessageId, nameof(envelope.MessageId));
        if (!KnownMessageTypes.Contains(envelope.MessageType))
        {
            throw new AgentProtocolValidationException($"Unknown protocol message type '{envelope.MessageType}'.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new AgentProtocolValidationException("Protocol payload must be a JSON object.");
        }

        var allowsTopLevelCredential = !isResponse && envelope.MessageType == "authenticate"
            || isResponse && envelope.MessageType == "registration_status";
        var isMaintenanceMessage = envelope.MessageType is "maintenance_status" or "archive_prepare" or "archive_commit" or "maintenance_sync";
        EnsureNoDenylistedFields(envelope.Payload, allowsTopLevelCredential, maintenancePayload: isMaintenanceMessage);

        if (isResponse)
        {
            ValidateResponseEnvelope(envelope);
            return;
        }

        ValidateRequestEnvelope(envelope);
    }

    private static void ValidateRequestEnvelope(ProtocolEnvelope envelope)
    {
        if (envelope.MessageType == "error" || HasAnyProperty(envelope.Payload, "result", "sources", "events", "dispatches"))
            throw new AgentProtocolValidationException("Response-only fields cannot appear in a request.");
        switch (envelope.MessageType)
        {
            case "agent_event": ValidateEvent(envelope.DeserializePayload<AgentEventMessage>()); break;
            case "dispatch_task": ValidateDispatch(envelope.DeserializePayload<DispatchTaskRequest>()); break;
            case "open_task": ValidateOpen(envelope.DeserializePayload<OpenTaskRequest>()); break;
            case "registration_request":
                if (HasAnyProperty(envelope.Payload, "source_instance_id", "adapter_version", "protocol_version", "request_nonce"))
                {
                    ValidateRegistration(envelope.DeserializePayload<RegistrationRequestMessage>());
                }
                else
                {
                    ValidateRegistration(envelope.DeserializePayload<AdapterRegistrationRequest>());
                }
                break;
            case "registration_approval": ValidateApproval(envelope.DeserializePayload<PairingApprovalMessage>()); break;
            case "registration_response": ValidateResponse(envelope.DeserializePayload<RegistrationResponse>()); break;
            case "registration_status":
                if (HasProperty(envelope.Payload, "status"))
                {
                    throw new AgentProtocolValidationException("Registration status response was supplied where a request was expected.");
                }

                ValidateRegistrationStatusRequest(envelope.DeserializePayload<RegistrationStatusRequest>());
                break;
            case "authenticate": ValidateAuthenticate(envelope.DeserializePayload<AuthenticateRequest>()); break;
            case "connection_test":
                if (HasAnyProperty(envelope.Payload, "relay_online", "app_online", "adapter_online", "protocol_version", "status", "observed_at_utc", "error"))
                {
                    throw new AgentProtocolValidationException("Connection test response was supplied where a request was expected.");
                }
                break;
            case "pending_sources":
                if (HasAnyProperty(envelope.Payload, "request_id", "source_type", "display_name", "source_instance_id", "adapter_version", "requested_at_utc", "expires_at_utc"))
                {
                    throw new AgentProtocolValidationException("Pending source response was supplied where a request was expected.");
                }
                break;
            case "decide_registration": ValidateDecision(envelope.DeserializePayload<RegistrationDecisionRequest>()); break;
            case "list_sources":
                if (HasAnyProperty(envelope.Payload, "source_type", "display_name", "source_instance_id", "adapter_version", "approved_at_utc", "enabled", "allowed_target_ids", "is_online"))
                {
                    throw new AgentProtocolValidationException("Approved source response was supplied where a request was expected.");
                }
                break;
            case "update_permissions": ValidatePermissions(envelope.DeserializePayload<UpdatePermissionsRequest>()); break;
            case "revoke_source": ValidateRevoke(envelope.DeserializePayload<RevokeSourceRequest>()); break;
            case "event_ack": ValidateEventAcknowledgement(envelope.DeserializePayload<EventAcknowledgementRequest>()); break;
            case "dispatch_ack": ValidateDispatchAcknowledgement(envelope.DeserializePayload<DispatchAcknowledgementRequest>()); break;
            case "maintenance_status":
                if (HasAnyProperty(envelope.Payload, "result", "counters", "oldest_archivable_at", "active_batch_id", "safe_error"))
                {
                    throw new AgentProtocolValidationException("Maintenance status response was supplied where a request was expected.");
                }

                break;
            case "archive_prepare":
                RejectMaintenanceResponseFields(envelope.Payload);
                ValidateArchivePrepare(envelope.DeserializePayload<AgentArchivePrepareRequest>());
                break;
            case "archive_commit":
                RejectMaintenanceResponseFields(envelope.Payload);
                ValidateArchiveCommit(envelope.DeserializePayload<AgentArchiveCommitRequest>());
                break;
            case "maintenance_sync":
                RejectMaintenanceSyncResponseFields(envelope.Payload);
                ValidateMaintenanceSync(envelope.DeserializePayload<AdapterMaintenanceSyncRequest>());
                break;
            case "status_check": break;
        }
    }

    private static void ValidateResponseEnvelope(ProtocolEnvelope envelope)
    {
        switch (envelope.MessageType)
        {
            case "registration_response": ValidateResponse(envelope.DeserializePayload<RegistrationResponse>()); break;
            case "registration_status":
                if (HasAnyProperty(envelope.Payload, "request_nonce", "source_type", "display_name", "adapter_version", "protocol_version"))
                {
                    throw new AgentProtocolValidationException("Registration status request fields cannot appear in a response.");
                }

                if (!HasProperty(envelope.Payload, "status"))
                {
                    throw new AgentProtocolValidationException("Registration status response must include status.");
                }

                ValidateRegistrationStatusResponse(envelope.DeserializePayload<RegistrationStatusResponse>());
                break;
            case "connection_test":
                if (!HasAnyProperty(envelope.Payload, "relay_online", "app_online", "adapter_online", "protocol_version", "status", "observed_at_utc", "error"))
                {
                    throw new AgentProtocolValidationException("Connection test response is missing its response payload.");
                }

                ValidateConnectionTest(envelope.DeserializePayload<RelayConnectionTestResponse>());
                break;
            case "pending_sources":
                ValidateResult(envelope, "pending_sources", "ok");
                ValidateSourceCollection<PendingSourceDto>(envelope, ValidatePendingSource);
                break;
            case "list_sources":
                ValidateResult(envelope, "list_sources", "ok");
                ValidateSourceCollection<ApprovedSourceDto>(envelope, ValidateApprovedSource);
                break;
            case "authenticate": ValidateResult(envelope, "authenticated", "unauthorized", "revoked"); break;
            case "agent_event":
                ValidateResult(envelope, "queued", "alreadyapplied", "already_applied", "disabled", "unauthorized", "revoked", "ok");
                break;
            case "dispatch_task":
                ValidateResult(envelope, "accepted", "alreadyapplied", "already_applied", "disabled", "offline", "unauthorized", "backpressure");
                ValidateOptionalIdentifier(envelope.Payload, "dispatch_request_id");
                ValidateOptionalIdentifier(envelope.Payload, "task_id");
                ValidateOptionalIdentifier(envelope.Payload, "source_instance");
                break;
            case "open_task": ValidateResult(envelope, "exact", "apponly", "app_only", "unsupported", "offline"); break;
            case "decide_registration":
            case "update_permissions":
            case "revoke_source": ValidateResult(envelope, "ok"); break;
            case "event_ack":
            case "dispatch_ack": ValidateResult(envelope, "acknowledged", "already_acknowledged", "unknown"); break;
            case "maintenance_status":
                ValidateResult(envelope, "status");
                ValidateMaintenanceStatus(envelope);
                break;
            case "archive_prepare":
                ValidateArchiveOperationResponse(envelope, "accepted", "already_prepared", "prepared", "rejected", "ok");
                break;
            case "archive_commit":
                ValidateArchiveOperationResponse(envelope, "accepted", "already_committed", "committed", "rejected", "ok");
                break;
            case "maintenance_sync": ValidateMaintenanceSyncResponse(envelope); break;
            case "status_check":
                ValidateResult(envelope, "status", "dispatches", "ok");
                ValidateEmbeddedEnvelopes(envelope.Payload, "events", "agent_event");
                ValidateEmbeddedEnvelopes(envelope.Payload, "dispatches", "dispatch_task");
                break;
            case "error": ValidateResult(envelope); break;
            default:
                throw new AgentProtocolValidationException($"Message type '{envelope.MessageType}' is request-only.");
        }
    }

    private static void ValidateResult(ProtocolEnvelope envelope, params string[] allowed)
    {
        if (!envelope.Payload.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
            throw new AgentProtocolValidationException("The relay response requires a result code.");
        var code = result.GetString();
        RequireText(code, "result");
        if (code!.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            || allowed.Length > 0 && !allowed.Contains(code, StringComparer.Ordinal))
            throw new AgentProtocolValidationException("The relay response result code is invalid.");
        ValidateOptionalIdentifier(envelope.Payload, "error");
    }

    private static void ValidateOptionalIdentifier(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String)
            throw new AgentProtocolValidationException("A relay response identifier must be text.");
        ValidateSafeIdentifier(value.GetString(), property);
    }

    private static void ValidateSourceCollection<T>(ProtocolEnvelope envelope, Action<T> validate)
    {
        if (!envelope.Payload.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            throw new AgentProtocolValidationException("The relay response requires a source collection.");
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
                throw new AgentProtocolValidationException("A source must be a JSON object.");
            validate((envelope with { Payload = source }).DeserializePayload<T>());
        }
    }

    private static void ValidateEmbeddedEnvelopes(JsonElement payload, string property, string messageType)
    {
        if (!payload.TryGetProperty(property, out var items)) return;
        if (items.ValueKind != JsonValueKind.Array)
            throw new AgentProtocolValidationException("A relay event or dispatch collection must be an array.");
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String and not JsonValueKind.Object)
                throw new AgentProtocolValidationException("An embedded envelope must be an object or JSON string.");
            var nested = ProtocolEnvelope.Parse(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
            if (nested.MessageType != messageType)
                throw new AgentProtocolValidationException("An embedded envelope has the wrong message type.");
            Validate(nested);
        }
    }

    public static bool IsValid(ProtocolEnvelope envelope)
    {
        try
        {
            Validate(envelope);
            return true;
        }
        catch (AgentProtocolValidationException)
        {
            return false;
        }
    }

    private static void ValidateEvent(AgentEventMessage message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        RequireText(message.SourceInstance, nameof(message.SourceInstance));
        RequireText(message.TaskId, nameof(message.TaskId));
        if (message.Sequence < 1) throw new AgentProtocolValidationException("Agent event sequence must be positive.");
        if (!AgentEventWireNames.IsKnown(message.EventType)) throw new AgentProtocolValidationException($"Unknown Agent event '{message.EventType}'.");
        _ = AgentPayloadSanitizer.Sanitize(message);

        if (string.Equals(message.EventType, "goal_completed", StringComparison.Ordinal))
        {
            var coveredTaskKeys = message.CoveredTaskKeys ?? Array.Empty<string>();
            if (coveredTaskKeys.Count == 0) throw new AgentProtocolValidationException("A completed goal must declare covered task keys.");
            var prefix = $"{message.SourceType}/{message.SourceInstance}/";
            if (coveredTaskKeys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw new AgentProtocolValidationException("Goal coverage cannot cross Agent source identities.");
            }
        }
    }

    private static void ValidateDispatch(DispatchTaskRequest message)
    {
        if (message.SourceType is not null || message.SourceInstanceId is not null)
        {
            ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
            ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        }
        RequireText(message.DispatchRequestId, nameof(message.DispatchRequestId));
        RequireText(message.TodoId, nameof(message.TodoId));
        RequireText(message.Title, nameof(message.Title));
        RequireText(message.TargetId, nameof(message.TargetId));
        AgentPayloadSanitizer.SanitizeText(message.DispatchRequestId, nameof(message.DispatchRequestId));
        AgentPayloadSanitizer.SanitizeText(message.TodoId, nameof(message.TodoId));
        AgentPayloadSanitizer.SanitizeText(message.Title, nameof(message.Title));
        AgentPayloadSanitizer.SanitizeText(message.Description, nameof(message.Description));
        AgentPayloadSanitizer.SanitizeText(message.Priority, nameof(message.Priority));
        if (AgentPayloadSanitizer.ContainsForbiddenText(message.TargetId)) throw new AgentProtocolValidationException("Target ID is not opaque.");
    }

    private static void ValidateOpen(OpenTaskRequest message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        RequireText(message.SourceInstance, nameof(message.SourceInstance));
        RequireText(message.TaskId, nameof(message.TaskId));
        AgentPayloadSanitizer.SanitizeText(message.SourceType, nameof(message.SourceType));
        AgentPayloadSanitizer.SanitizeText(message.SourceInstance, nameof(message.SourceInstance));
        AgentPayloadSanitizer.SanitizeText(message.TaskId, nameof(message.TaskId));
    }

    private static void ValidateRegistration(AdapterRegistrationRequest message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        RequireText(message.DisplayName, nameof(message.DisplayName));
        RequireText(message.Version, nameof(message.Version));
        AgentPayloadSanitizer.SanitizeText(message.SourceType, nameof(message.SourceType));
        AgentPayloadSanitizer.SanitizeText(message.DisplayName, nameof(message.DisplayName));
        AgentPayloadSanitizer.SanitizeText(message.Version, nameof(message.Version));
    }

    private static void ValidateRegistration(RegistrationRequestMessage message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.DisplayName, nameof(message.DisplayName));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        ValidateSafeIdentifier(message.AdapterVersion, nameof(message.AdapterVersion));
        ValidateProtocolVersion(message.ProtocolVersion, nameof(message.ProtocolVersion));
        ValidateNonce(message.RequestNonce);
    }

    private static void ValidateRegistrationStatusRequest(RegistrationStatusRequest message)
    {
        ValidateSafeIdentifier(message.RequestId, nameof(message.RequestId));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        ValidateNonce(message.RequestNonce);
    }

    private static void ValidateRegistrationStatusResponse(RegistrationStatusResponse message)
    {
        RequireKnown(message.Status, KnownRegistrationStatuses, nameof(message.Status));
        ValidateSafeIdentifier(message.RequestId, nameof(message.RequestId));
        if (message.SourceInstanceId is not null)
        {
            ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        }

        if (message.Credential is not null)
        {
            if (!string.Equals(message.Status, "approved", StringComparison.Ordinal))
            {
                throw new AgentProtocolValidationException("A credential may only be returned for an approved registration.");
            }

            ValidateCredential(message.Credential);
        }

        ValidateOptionalError(message.Error);
    }

    private static void ValidateAuthenticate(AuthenticateRequest message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        ValidateCredential(message.Credential);
    }

    private static void ValidateConnectionTest(RelayConnectionTestResponse message)
    {
        ValidateProtocolVersion(message.ProtocolVersion, nameof(message.ProtocolVersion));
        RequireKnown(message.Status, KnownConnectionStatuses, nameof(message.Status));
        ValidateOptionalError(message.Error);
    }

    private static void ValidatePendingSource(PendingSourceDto message)
    {
        ValidateSafeIdentifier(message.RequestId, nameof(message.RequestId));
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.DisplayName, nameof(message.DisplayName));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        ValidateSafeIdentifier(message.AdapterVersion, nameof(message.AdapterVersion));
    }

    private static void ValidateApprovedSource(ApprovedSourceDto message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.DisplayName, nameof(message.DisplayName));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        ValidateSafeIdentifier(message.AdapterVersion, nameof(message.AdapterVersion));
        ValidateTargetIds(message.AllowedTargetIds);
    }

    private static void ValidateDecision(RegistrationDecisionRequest message)
    {
        ValidateSafeIdentifier(message.RequestId, nameof(message.RequestId));
        RequireKnown(message.Decision, KnownRegistrationDecisions, nameof(message.Decision));
    }

    private static void ValidatePermissions(UpdatePermissionsRequest message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        ValidateTargetIds(message.AllowedTargetIds);
    }

    private static void ValidateRevoke(RevokeSourceRequest message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
    }

    private static void ValidateEventAcknowledgement(EventAcknowledgementRequest message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        if (message.EventKeys is null || message.EventKeys.Count == 0 || message.EventKeys.Count > 512)
            throw new AgentProtocolValidationException("Event acknowledgement must contain a bounded non-empty collection.");
        foreach (var key in message.EventKeys)
        {
            if (key is null) throw new AgentProtocolValidationException("Event acknowledgement keys cannot be null.");
            ValidateSafeIdentifier(key.TaskId, nameof(key.TaskId));
            if (key.Sequence < 1) throw new AgentProtocolValidationException("Event acknowledgement sequence must be positive.");
        }
    }

    private static void ValidateDispatchAcknowledgement(DispatchAcknowledgementRequest message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.SourceInstanceId, nameof(message.SourceInstanceId));
        if (message.DispatchRequestIds is null || message.DispatchRequestIds.Count == 0 || message.DispatchRequestIds.Count > 512)
            throw new AgentProtocolValidationException("Dispatch acknowledgement must contain a bounded non-empty collection.");
        foreach (var requestId in message.DispatchRequestIds)
            ValidateSafeIdentifier(requestId, nameof(requestId));
    }

    private static void RejectMaintenanceResponseFields(JsonElement payload)
    {
        if (HasAnyProperty(payload, "result", "error", "counters", "oldest_archivable_at", "active_batch_id", "safe_error"))
        {
            throw new AgentProtocolValidationException("Maintenance response fields cannot appear in a request.");
        }
    }

    private static void RejectMaintenanceSyncResponseFields(JsonElement payload)
    {
        if (HasAnyProperty(payload, "result", "error", "batch_id", "items", "batch_sha256", "operation"))
        {
            throw new AgentProtocolValidationException("Maintenance sync response fields cannot appear in a request.");
        }
    }

    private static void ValidateArchivePrepare(AgentArchivePrepareRequest message)
    {
        ValidateSafeIdentifier(message.BatchId, nameof(message.BatchId));
        ValidateSha256(message.BatchSha256, nameof(message.BatchSha256));

        if (message.Items is null || message.Items.Count is 0 or > 128)
        {
            throw new AgentProtocolValidationException("Archive prepare must contain between 1 and 128 items.");
        }

        var identities = new HashSet<(string SourceType, string SourceInstance, string TaskId, string DispatchRequestId)>();
        foreach (var item in message.Items)
        {
            if (item is null)
            {
                throw new AgentProtocolValidationException("Archive prepare items cannot be null.");
            }

            ValidateArchiveItem(item);
            var identity = (
                item.SourceType.Trim(),
                item.SourceInstance.Trim(),
                item.TaskId.Trim(),
                item.DispatchRequestId.Trim());
            if (!identities.Add(identity))
            {
                throw new AgentProtocolValidationException("Archive prepare cannot contain duplicate identities.");
            }
        }
    }

    private static void ValidateArchiveItem(AgentArchiveProtocolItem item)
    {
        ValidateSafeIdentifier(item.SourceType, nameof(item.SourceType));
        ValidateSafeIdentifier(item.SourceInstance, nameof(item.SourceInstance));
        ValidateSafeIdentifier(item.TaskId, nameof(item.TaskId));
        ValidateSafeIdentifier(item.DispatchRequestId, nameof(item.DispatchRequestId));
        ValidateSafeIdentifier(item.ExecutionId, nameof(item.ExecutionId));
        if (item.FinalSequence < 0)
        {
            throw new AgentProtocolValidationException("Archive item final sequence cannot be negative.");
        }

        RequireKnown(item.FinalStatus, KnownTerminalStatuses, nameof(item.FinalStatus));
        if (item.EndedAt == default || item.EndedAt == DateTimeOffset.MinValue)
        {
            throw new AgentProtocolValidationException("Archive items require an ended-at timestamp.");
        }

        ValidateSha256(item.SummarySha256, nameof(item.SummarySha256));
    }

    private static void ValidateArchiveCommit(AgentArchiveCommitRequest message)
    {
        ValidateSafeIdentifier(message.BatchId, nameof(message.BatchId));
        ValidateSha256(message.BatchSha256, nameof(message.BatchSha256));
    }

    private static void ValidateMaintenanceStatus(ProtocolEnvelope envelope)
    {
        var response = envelope.DeserializePayload<AgentMaintenanceStatusResponse>();
        if (response.Counters is null)
        {
            throw new AgentProtocolValidationException("Maintenance status requires capacity counters.");
        }

        foreach (var counter in response.Counters)
        {
            ValidateCapacityCounter(counter);
        }

        ValidateOptionalIdentifier(envelope.Payload, "active_batch_id");
        ValidateOptionalError(response.SafeError);
        ValidateOptionalErrorFromPayload(envelope.Payload, "error");
    }

    private static void ValidateMaintenanceSync(AdapterMaintenanceSyncRequest message)
    {
        ValidateSafeIdentifier(message.SourceType, nameof(message.SourceType));
        ValidateSafeIdentifier(message.SourceInstance, nameof(message.SourceInstance));
        ValidateCapacityCounter(message.AdapterJournal);

        var hasBatch = message.AcknowledgedBatchId is not null;
        var hasPhase = message.AcknowledgedPhase is not null;
        if (hasBatch != hasPhase)
        {
            throw new AgentProtocolValidationException("Acknowledged batch and phase must be supplied together.");
        }

        if (hasBatch)
        {
            ValidateSafeIdentifier(message.AcknowledgedBatchId, nameof(message.AcknowledgedBatchId));
            RequireKnown(message.AcknowledgedPhase, KnownAcknowledgementPhases, nameof(message.AcknowledgedPhase));
        }
        else if (message.SafeError is not null)
        {
            throw new AgentProtocolValidationException("A maintenance error must acknowledge a batch and phase.");
        }

        ValidateOptionalError(message.SafeError);
    }

    private static void ValidateMaintenanceSyncResponse(ProtocolEnvelope envelope)
    {
        var result = ReadRequiredResult(envelope);
        switch (result)
        {
            case "none":
                RejectUnexpectedNoneFields(envelope.Payload);
                break;
            case "prepare":
                RequireProperties(envelope.Payload, "batch_id", "items", "batch_sha256");
                var items = DeserializeRequiredArray(envelope, "items");
                ValidateArchivePrepare(new AgentArchivePrepareRequest(
                    ReadRequiredString(envelope.Payload, "batch_id"),
                    items,
                    ReadRequiredString(envelope.Payload, "batch_sha256")));
                ValidateOptionalSyncSource(envelope.Payload, items);
                RejectUnexpectedSyncCommandFields(envelope.Payload, requireBatch: true, requireItems: true);
                break;
            case "commit":
                RequireProperties(envelope.Payload, "batch_id", "batch_sha256");
                ValidateArchiveCommit(new AgentArchiveCommitRequest(
                    ReadRequiredString(envelope.Payload, "batch_id"),
                    ReadRequiredString(envelope.Payload, "batch_sha256")));
                ValidateOptionalSourceIdentity(envelope.Payload);
                RejectUnexpectedSyncCommandFields(envelope.Payload, requireBatch: true, requireItems: false);
                break;
            case "prepared":
                ValidateMaintenanceAcknowledgementResult(envelope, "prepare", requireError: false);
                break;
            case "committed":
                ValidateMaintenanceAcknowledgementResult(envelope, "commit", requireError: false);
                break;
            case "rejected":
                ValidateMaintenanceAcknowledgementResult(envelope, phase: null, requireError: true);
                break;
            default:
                throw new AgentProtocolValidationException("The maintenance sync result code is invalid.");
        }
    }

    private static void ValidateMaintenanceAcknowledgementResult(
        ProtocolEnvelope envelope,
        string? phase,
        bool requireError)
    {
        RequireProperties(envelope.Payload, "acknowledged_batch_id", "acknowledged_phase");
        var acknowledgedPhase = ReadRequiredString(envelope.Payload, "acknowledged_phase");
        RequireKnown(acknowledgedPhase, KnownAcknowledgementPhases, "acknowledged_phase");
        if (phase is not null && !string.Equals(acknowledgedPhase, phase, StringComparison.Ordinal))
        {
            throw new AgentProtocolValidationException("The acknowledgement phase does not match the result.");
        }

        ValidateSafeIdentifier(ReadRequiredString(envelope.Payload, "acknowledged_batch_id"), "acknowledged_batch_id");
        ValidateOptionalSourceIdentity(envelope.Payload);
        var safeError = ReadOptionalString(envelope.Payload, "safe_error");
        if (requireError && safeError is null)
        {
            throw new AgentProtocolValidationException("A rejected maintenance acknowledgement requires a safe error.");
        }

        if (!requireError && safeError is not null)
        {
            throw new AgentProtocolValidationException("A successful maintenance acknowledgement cannot carry a safe error.");
        }

        ValidateOptionalError(safeError);
        ValidateOptionalErrorFromPayload(envelope.Payload, "error");
    }

    private static void ValidateOptionalMaintenanceBatchFields(JsonElement payload)
    {
        if (HasProperty(payload, "batch_id"))
        {
            ValidateOptionalIdentifier(payload, "batch_id");
        }

        if (HasProperty(payload, "batch_sha256"))
        {
            var hash = ReadRequiredString(payload, "batch_sha256");
            ValidateSha256(hash, "batch_sha256");
        }

        if (HasProperty(payload, "safe_error"))
        {
            ValidateOptionalErrorFromPayload(payload, "safe_error");
        }
    }

    private static void ValidateArchiveOperationResponse(ProtocolEnvelope envelope, params string[] allowedResults)
    {
        ValidateResult(envelope, allowedResults);
        ValidateOptionalMaintenanceBatchFields(envelope.Payload);
        var result = ReadRequiredResult(envelope);
        var safeError = ReadOptionalString(envelope.Payload, "safe_error");
        if (string.Equals(result, "rejected", StringComparison.Ordinal) && safeError is null)
        {
            throw new AgentProtocolValidationException("A rejected archive operation requires a safe error.");
        }

        if (!string.Equals(result, "rejected", StringComparison.Ordinal) && safeError is not null)
        {
            throw new AgentProtocolValidationException("A successful archive operation cannot carry a safe error.");
        }
    }

    private static void ValidateCapacityCounter(AgentCapacityCounter? counter)
    {
        if (counter is null)
        {
            throw new AgentProtocolValidationException("Capacity counters cannot be null.");
        }

        ValidateSafeIdentifier(counter.Name, nameof(counter.Name));
        if (counter.Used < 0 || counter.Archivable < 0)
        {
            throw new AgentProtocolValidationException("Capacity counters cannot be negative.");
        }

        if (counter.Limit <= 0 || counter.Used > counter.Limit || counter.Archivable > counter.Limit)
        {
            throw new AgentProtocolValidationException("Capacity counter values exceed their positive limit.");
        }
    }

    private static void ValidateSha256(string? value, string fieldName)
    {
        if (value is null || value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new AgentProtocolValidationException($"Protocol field '{fieldName}' must be an uppercase 64-character SHA-256 hash.");
        }
    }

    private static void RejectUnexpectedSyncCommandFields(JsonElement payload, bool requireBatch, bool requireItems)
    {
        if (requireBatch && !HasAnyProperty(payload, "batch_id", "batch_sha256"))
        {
            throw new AgentProtocolValidationException("A maintenance command requires batch identity and hash.");
        }

        if (requireItems && !HasProperty(payload, "items"))
        {
            throw new AgentProtocolValidationException("A prepare command requires archive items.");
        }

        if (HasAnyProperty(payload, "acknowledged_batch_id", "acknowledged_phase"))
        {
            throw new AgentProtocolValidationException("A maintenance command cannot carry acknowledgement fields.");
        }

        if (HasAnyProperty(payload, "safe_error", "error"))
        {
            throw new AgentProtocolValidationException("A maintenance command cannot carry an error field.");
        }

        if (!requireBatch && HasAnyProperty(payload, "batch_id", "items", "batch_sha256"))
        {
            throw new AgentProtocolValidationException("A no-op maintenance command cannot carry batch or acknowledgement fields.");
        }
    }

    private static void RejectUnexpectedNoneFields(JsonElement payload)
    {
        if (payload.EnumerateObject().Any(property => !string.Equals(property.Name, "result", StringComparison.OrdinalIgnoreCase)))
        {
            throw new AgentProtocolValidationException("A no-op maintenance command cannot carry additional fields.");
        }
    }

    private static void ValidateOptionalSyncSource(
        JsonElement payload,
        IReadOnlyList<AgentArchiveProtocolItem> items)
    {
        var hasSourceType = HasProperty(payload, "source_type");
        var hasSourceInstance = HasProperty(payload, "source_instance");
        if (hasSourceType != hasSourceInstance)
        {
            throw new AgentProtocolValidationException("A maintenance command source identity is incomplete.");
        }

        if (!hasSourceType) return;
        var sourceType = ReadRequiredString(payload, "source_type");
        var sourceInstance = ReadRequiredString(payload, "source_instance");
        ValidateSafeIdentifier(sourceType, "source_type");
        ValidateSafeIdentifier(sourceInstance, "source_instance");
        if (items.Any(item => !string.Equals(item.SourceType, sourceType, StringComparison.Ordinal)
            || !string.Equals(item.SourceInstance, sourceInstance, StringComparison.Ordinal)))
        {
            throw new AgentProtocolValidationException("A maintenance command source identity does not match its archive items.");
        }
    }

    private static void ValidateOptionalSourceIdentity(JsonElement payload)
    {
        var hasSourceType = HasProperty(payload, "source_type");
        var hasSourceInstance = HasProperty(payload, "source_instance");
        if (hasSourceType != hasSourceInstance)
        {
            throw new AgentProtocolValidationException("A maintenance acknowledgement source identity is incomplete.");
        }

        if (hasSourceType)
        {
            ValidateSafeIdentifier(ReadRequiredString(payload, "source_type"), "source_type");
            ValidateSafeIdentifier(ReadRequiredString(payload, "source_instance"), "source_instance");
        }
    }

    private static string ReadRequiredResult(ProtocolEnvelope envelope)
    {
        if (!envelope.Payload.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
        {
            throw new AgentProtocolValidationException("The maintenance sync response requires a result code.");
        }

        var value = result.GetString();
        RequireText(value, "result");
        return value!;
    }

    private static void RequireProperties(JsonElement payload, params string[] names)
    {
        if (names.Any(name => !HasProperty(payload, name)))
        {
            throw new AgentProtocolValidationException("A maintenance payload is missing required fields.");
        }
    }

    private static string ReadRequiredString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new AgentProtocolValidationException($"Maintenance field '{name}' must be text.");
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AgentProtocolValidationException($"Maintenance field '{name}' must be text.");
        }

        return value.GetString();
    }

    private static IReadOnlyList<AgentArchiveProtocolItem> DeserializeRequiredArray(ProtocolEnvelope envelope, string name)
    {
        if (!envelope.Payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new AgentProtocolValidationException($"Maintenance field '{name}' must be an array.");
        }

        try
        {
            return value.Deserialize<IReadOnlyList<AgentArchiveProtocolItem>>(ProtocolEnvelope.JsonOptions)
                ?? throw new AgentProtocolValidationException($"Maintenance field '{name}' cannot be null.");
        }
        catch (JsonException error)
        {
            throw new AgentProtocolValidationException($"Maintenance field '{name}' could not be decoded.", error);
        }
    }

    private static void ValidateOptionalErrorFromPayload(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AgentProtocolValidationException($"A relay response {property} must be text.");
        }

        ValidateOptionalError(value.GetString());
    }

    private static void ValidateApproval(PairingApprovalMessage message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        RequireText(message.RequestId, nameof(message.RequestId));
    }

    private static void ValidateResponse(RegistrationResponse message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        if (message.Approved) RequireText(message.SourceInstance, nameof(message.SourceInstance));
    }

    private static void EnsureNoDenylistedFields(
        JsonElement element,
        bool allowTopLevelCredential,
        int depth = 0,
        bool maintenancePayload = false)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var isTopLevelCredential = depth == 0
                    && string.Equals(property.Name, "credential", StringComparison.OrdinalIgnoreCase);
                var denylisted = DenylistedFields.Contains(property.Name)
                    || maintenancePayload && MaintenanceDenylistedFields.Contains(property.Name);
                if (denylisted && !(allowTopLevelCredential && isTopLevelCredential))
                {
                    throw new AgentProtocolValidationException($"Payload field '{property.Name}' is not allowed.");
                }

                EnsureNoDenylistedFields(property.Value, allowTopLevelCredential, depth + 1, maintenancePayload);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) EnsureNoDenylistedFields(child, allowTopLevelCredential, depth + 1, maintenancePayload);
        }
    }

    private static void RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw new AgentProtocolValidationException($"Protocol field '{fieldName}' is required and bounded.");
        }
    }

    private static void ValidateSafeIdentifier(string? value, string fieldName)
    {
        RequireText(value, fieldName);
        if (value!.Any(char.IsControl)) throw new AgentProtocolValidationException("An identifier cannot contain control characters.");
        AgentPayloadSanitizer.SanitizeText(value, fieldName);
    }

    private static void ValidateProtocolVersion(string? value, string fieldName)
    {
        RequireText(value, fieldName);
        if (!string.Equals(value, ProtocolEnvelope.CurrentProtocolVersion, StringComparison.Ordinal))
        {
            throw new AgentProtocolValidationException($"Unsupported protocol version '{value}'.");
        }
    }

    private static void ValidateNonce(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !IsHex(character)))
        {
            throw new AgentProtocolValidationException("Registration request nonce must be exactly 64 hexadecimal characters.");
        }
    }

    private static void ValidateCredential(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new AgentProtocolValidationException("Credential is required.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException error)
        {
            throw new AgentProtocolValidationException("Credential must be base64.", error);
        }

        if (decoded.Length != 32 || !string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
        {
            throw new AgentProtocolValidationException("Credential must be canonical base64 for exactly 32 bytes.");
        }
    }

    private static void ValidateTargetIds(IReadOnlyList<string>? targetIds)
    {
        if (targetIds is null)
        {
            throw new AgentProtocolValidationException("Allowed target IDs are required.");
        }

        foreach (var targetId in targetIds)
        {
            RequireText(targetId, nameof(targetIds));
            if (AgentPayloadSanitizer.ContainsForbiddenText(targetId))
            {
                throw new AgentProtocolValidationException("Target ID is not opaque.");
            }
        }
    }

    private static void ValidateOptionalError(string? error)
    {
        if (error is null) return;
        ValidateSafeIdentifier(error, nameof(error));
    }

    private static void RequireKnown(string? value, HashSet<string> knownValues, string fieldName)
    {
        RequireText(value, fieldName);
        if (value is null || !knownValues.Contains(value))
        {
            throw new AgentProtocolValidationException($"Unknown {fieldName} '{value}'.");
        }
    }

    private static bool HasProperty(JsonElement payload, string name) =>
        payload.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyProperty(JsonElement payload, params string[] names) =>
        names.Any(name => HasProperty(payload, name));

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
