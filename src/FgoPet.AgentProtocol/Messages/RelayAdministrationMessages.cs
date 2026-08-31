using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

public sealed record RelayConnectionTestResponse(
    [property: JsonPropertyName("relay_online")] bool RelayOnline,
    [property: JsonPropertyName("app_online")] bool AppOnline,
    [property: JsonPropertyName("adapter_online")] bool AdapterOnline,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observed_at_utc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("error")] string? Error);

public sealed record PendingSourceDto(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("requested_at_utc")] DateTimeOffset RequestedAtUtc,
    [property: JsonPropertyName("expires_at_utc")] DateTimeOffset ExpiresAtUtc);

public sealed record ApprovedSourceDto(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("approved_at_utc")] DateTimeOffset ApprovedAtUtc,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("allowed_target_ids")] IReadOnlyList<string> AllowedTargetIds,
    [property: JsonPropertyName("is_online")] bool IsOnline);

public sealed record RegistrationDecisionRequest(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("decision")] string Decision);

public sealed record UpdatePermissionsRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("allowed_target_ids")] IReadOnlyList<string> AllowedTargetIds,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record RevokeSourceRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId);

/// <summary>Application acknowledgement for events that have been durably projected.</summary>
public sealed record EventAcknowledgementRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("event_keys")] IReadOnlyList<EventAcknowledgement> EventKeys);

public sealed record EventAcknowledgement(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("sequence")] long Sequence);

/// <summary>Adapter acknowledgement after a dispatch is durably journaled.</summary>
public sealed record DispatchAcknowledgementRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("dispatch_request_ids")] IReadOnlyList<string> DispatchRequestIds);
