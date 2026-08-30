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
        "registration_approval", "registration_response", "status_check",
    };

    public static void Validate(ProtocolEnvelope envelope)
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

        EnsureNoDenylistedFields(envelope.Payload);
        switch (envelope.MessageType)
        {
            case "agent_event": ValidateEvent(envelope.DeserializePayload<AgentEventMessage>()); break;
            case "dispatch_task": ValidateDispatch(envelope.DeserializePayload<DispatchTaskRequest>()); break;
            case "open_task": ValidateOpen(envelope.DeserializePayload<OpenTaskRequest>()); break;
            case "registration_request": ValidateRegistration(envelope.DeserializePayload<AdapterRegistrationRequest>()); break;
            case "registration_approval": ValidateApproval(envelope.DeserializePayload<PairingApprovalMessage>()); break;
            case "registration_response": ValidateResponse(envelope.DeserializePayload<RegistrationResponse>()); break;
            case "status_check": break;
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
            if (message.CoveredTaskKeys.Count == 0) throw new AgentProtocolValidationException("A completed goal must declare covered task keys.");
            var prefix = $"{message.SourceType}/{message.SourceInstance}/";
            if (message.CoveredTaskKeys.Any(key => !key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw new AgentProtocolValidationException("Goal coverage cannot cross Agent source identities.");
            }
        }
    }

    private static void ValidateDispatch(DispatchTaskRequest message)
    {
        RequireText(message.DispatchRequestId, nameof(message.DispatchRequestId));
        RequireText(message.TodoId, nameof(message.TodoId));
        RequireText(message.Title, nameof(message.Title));
        RequireText(message.TargetId, nameof(message.TargetId));
        AgentPayloadSanitizer.SanitizeText(message.Title, nameof(message.Title));
        AgentPayloadSanitizer.SanitizeText(message.Description, nameof(message.Description));
        if (AgentPayloadSanitizer.ContainsForbiddenText(message.TargetId)) throw new AgentProtocolValidationException("Target ID is not opaque.");
    }

    private static void ValidateOpen(OpenTaskRequest message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        RequireText(message.SourceInstance, nameof(message.SourceInstance));
        RequireText(message.TaskId, nameof(message.TaskId));
    }

    private static void ValidateRegistration(AdapterRegistrationRequest message)
    {
        RequireText(message.SourceType, nameof(message.SourceType));
        RequireText(message.DisplayName, nameof(message.DisplayName));
        RequireText(message.Version, nameof(message.Version));
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

    private static void EnsureNoDenylistedFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (DenylistedFields.Contains(property.Name))
                {
                    throw new AgentProtocolValidationException($"Payload field '{property.Name}' is not allowed.");
                }

                EnsureNoDenylistedFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) EnsureNoDenylistedFields(child);
        }
    }

    private static void RequireText(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw new AgentProtocolValidationException($"Protocol field '{fieldName}' is required and bounded.");
        }
    }
}
