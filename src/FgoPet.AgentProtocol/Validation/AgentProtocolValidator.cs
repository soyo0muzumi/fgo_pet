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

    private static readonly HashSet<string> KnownMessageTypes = new(StringComparer.Ordinal)
    {
        "agent_event", "dispatch_task", "open_task", "registration_request",
        "registration_approval", "registration_response", "registration_status",
        "authenticate", "connection_test", "pending_sources", "decide_registration",
        "list_sources", "update_permissions", "revoke_source", "status_check", "error",
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
        EnsureNoDenylistedFields(envelope.Payload, allowsTopLevelCredential);

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
                ValidateResult(envelope, "accepted", "alreadyapplied", "already_applied", "disabled", "offline", "unauthorized");
                ValidateOptionalIdentifier(envelope.Payload, "dispatch_request_id");
                ValidateOptionalIdentifier(envelope.Payload, "task_id");
                ValidateOptionalIdentifier(envelope.Payload, "source_instance");
                break;
            case "open_task": ValidateResult(envelope, "exact", "apponly", "app_only", "unsupported", "offline"); break;
            case "decide_registration":
            case "update_permissions":
            case "revoke_source": ValidateResult(envelope, "ok"); break;
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

    private static void EnsureNoDenylistedFields(JsonElement element, bool allowTopLevelCredential, int depth = 0)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var isTopLevelCredential = depth == 0
                    && string.Equals(property.Name, "credential", StringComparison.OrdinalIgnoreCase);
                if (DenylistedFields.Contains(property.Name) && !(allowTopLevelCredential && isTopLevelCredential))
                {
                    throw new AgentProtocolValidationException($"Payload field '{property.Name}' is not allowed.");
                }

                EnsureNoDenylistedFields(property.Value, allowTopLevelCredential, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) EnsureNoDenylistedFields(child, allowTopLevelCredential, depth + 1);
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
