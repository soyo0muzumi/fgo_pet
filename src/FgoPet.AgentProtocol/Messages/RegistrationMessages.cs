using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

public sealed record AdapterRegistrationRequest
{
    public AdapterRegistrationRequest()
    {
    }

    public AdapterRegistrationRequest(string sourceType, string displayName, string version)
    {
        SourceType = sourceType;
        DisplayName = displayName;
        Version = version;
    }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record PairingApprovalMessage
{
    public PairingApprovalMessage()
    {
    }

    public PairingApprovalMessage(string sourceType, string requestId, bool approved)
    {
        SourceType = sourceType;
        RequestId = requestId;
        Approved = approved;
    }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("request_id")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("approved")]
    public bool Approved { get; init; }
}

public sealed record RegistrationResponse
{
    public RegistrationResponse()
    {
    }

    public RegistrationResponse(string sourceType, string sourceInstance, bool approved, string? credential = null)
    {
        SourceType = sourceType;
        SourceInstance = sourceInstance;
        Approved = approved;
        Credential = credential;
    }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("source_instance")]
    public string? SourceInstance { get; init; }

    [JsonPropertyName("approved")]
    public bool Approved { get; init; }

    [JsonPropertyName("credential")]
    public string? Credential { get; init; }
}

/// <summary>
/// Versioned registration request sent by an adapter. The source instance and
/// nonce make a request specific to one durable adapter identity.
/// </summary>
public sealed record RegistrationRequestMessage(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("request_nonce")] string RequestNonce);

public sealed record RegistrationStatusRequest(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("request_nonce")] string RequestNonce);

public sealed record RegistrationStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("source_instance_id")] string? SourceInstanceId,
    [property: JsonPropertyName("credential")] string? Credential,
    [property: JsonPropertyName("error")] string? Error);

public sealed record AuthenticateRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance_id")] string SourceInstanceId,
    [property: JsonPropertyName("credential")] string Credential);
