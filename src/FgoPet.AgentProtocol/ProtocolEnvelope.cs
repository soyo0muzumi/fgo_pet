using System.Text.Json;
using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol;

public sealed record ProtocolEnvelope
{
    public const string CurrentProtocolVersion = "1";

    public ProtocolEnvelope(
        string protocolVersion,
        string messageId,
        string messageType,
        DateTimeOffset sentAt,
        JsonElement payload)
    {
        ProtocolVersion = protocolVersion;
        MessageId = messageId;
        MessageType = messageType;
        SentAt = sentAt;
        Payload = payload;
    }

    [JsonPropertyName("protocol_version")]
    public string ProtocolVersion { get; init; }

    [JsonPropertyName("message_id")]
    public string MessageId { get; init; }

    [JsonPropertyName("message_type")]
    public string MessageType { get; init; }

    [JsonPropertyName("sent_at")]
    public DateTimeOffset SentAt { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    public static ProtocolEnvelope Create<T>(
        string messageId,
        string messageType,
        T payload,
        DateTimeOffset? sentAt = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new ProtocolEnvelope(
            CurrentProtocolVersion,
            messageId,
            messageType,
            sentAt ?? DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
    }

    public static ProtocolEnvelope Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new ProtocolEnvelope(
                root.GetProperty("protocol_version").GetString() ?? string.Empty,
                root.GetProperty("message_id").GetString() ?? string.Empty,
                root.GetProperty("message_type").GetString() ?? string.Empty,
                root.GetProperty("sent_at").GetDateTimeOffset(),
                root.GetProperty("payload").Clone());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AgentProtocolValidationException("The protocol envelope is not valid JSON v1 shape.", error);
        }
    }

    public T DeserializePayload<T>()
    {
        try
        {
            return Payload.Deserialize<T>(JsonOptions)
                ?? throw new AgentProtocolValidationException("The protocol payload is null.");
        }
        catch (JsonException error)
        {
            throw new AgentProtocolValidationException("The protocol payload could not be decoded.", error);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class AgentProtocolValidationException : InvalidOperationException
{
    public AgentProtocolValidationException(string message)
        : base(message)
    {
    }

    public AgentProtocolValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
