using System.Text.Json.Serialization;

namespace FgoPet.AgentRelay.Storage;

/// <summary>Durable authorization state. Queues and online flags intentionally remain in memory.</summary>
public sealed record RelayState
{
    public RelayState(
        int schemaVersion = 1,
        IReadOnlyList<PendingRegistration>? pending = null,
        IReadOnlyList<RegistrationGrant>? grants = null)
    {
        SchemaVersion = schemaVersion;
        Pending = pending ?? Array.Empty<PendingRegistration>();
        Grants = grants ?? Array.Empty<RegistrationGrant>();
    }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("pending")]
    public IReadOnlyList<PendingRegistration> Pending { get; init; }

    [JsonPropertyName("grants")]
    public IReadOnlyList<RegistrationGrant> Grants { get; init; }

    public static RelayState Empty { get; } = new();
}
