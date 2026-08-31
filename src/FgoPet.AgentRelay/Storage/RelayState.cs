using System.Text.Json.Serialization;

namespace FgoPet.AgentRelay.Storage;

/// <summary>Durable authorization, delivery and bounded event sequence state. Online flags remain in memory.</summary>
public sealed record RelayState
{
    public RelayState(
        int schemaVersion = 1,
        IReadOnlyList<PendingRegistration>? pending = null,
        IReadOnlyList<RegistrationGrant>? grants = null,
        IReadOnlyList<QueuedInboundEvent>? inbound = null,
        IReadOnlyList<QueuedDispatch>? outbound = null,
        IReadOnlyList<DispatchReceipt>? dispatchReceipts = null,
        IReadOnlyList<string>? inboundEventKeys = null,
        IReadOnlyList<InboundEventWatermark>? inboundEventWatermarks = null)
    {
        SchemaVersion = schemaVersion;
        Pending = pending ?? Array.Empty<PendingRegistration>();
        Grants = grants ?? Array.Empty<RegistrationGrant>();
        Inbound = inbound ?? Array.Empty<QueuedInboundEvent>();
        Outbound = outbound ?? Array.Empty<QueuedDispatch>();
        DispatchReceipts = dispatchReceipts ?? Array.Empty<DispatchReceipt>();
        InboundEventKeys = inboundEventKeys ?? Array.Empty<string>();
        InboundEventWatermarks = inboundEventWatermarks ?? Array.Empty<InboundEventWatermark>();
    }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("pending")]
    public IReadOnlyList<PendingRegistration> Pending { get; init; }

    [JsonPropertyName("grants")]
    public IReadOnlyList<RegistrationGrant> Grants { get; init; }

    [JsonPropertyName("inbound")]
    public IReadOnlyList<QueuedInboundEvent> Inbound { get; init; }

    [JsonPropertyName("outbound")]
    public IReadOnlyList<QueuedDispatch> Outbound { get; init; }

    [JsonPropertyName("dispatch_receipts")]
    public IReadOnlyList<DispatchReceipt> DispatchReceipts { get; init; }

    [JsonPropertyName("inbound_event_keys")]
    public IReadOnlyList<string> InboundEventKeys { get; init; }

    [JsonPropertyName("inbound_event_watermarks")]
    public IReadOnlyList<InboundEventWatermark> InboundEventWatermarks { get; init; }

    public static RelayState Empty { get; } = new();
}
