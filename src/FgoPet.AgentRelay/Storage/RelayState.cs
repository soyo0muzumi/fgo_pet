using System.Text.Json.Serialization;
using FgoPet.AgentProtocol.Messages;

namespace FgoPet.AgentRelay.Storage;

public enum RelayArchiveBatchPhase
{
    AwaitingAdapterPrepare,
    Prepared,
    AwaitingAdapterCommit,
    Completed,
    Rejected,
}

/// <summary>
/// Durable relay-side summary of a coordinated adapter archive. The item list
/// contains opaque identities and hashes only; it never carries task content.
/// </summary>
public sealed record RelayArchiveBatchState(
    string BatchId,
    string SourceType,
    string SourceInstance,
    DateTimeOffset CreatedAt,
    RelayArchiveBatchPhase Phase,
    IReadOnlyList<AgentArchiveProtocolItem> Items,
    string BatchSha256,
    string? SafeError = null);

/// <summary>A replay fence retained after a relay archive commit.</summary>
public sealed record AgentArchiveTombstone(
    string SourceType,
    string SourceInstance,
    string TaskId,
    string DispatchRequestId,
    long FinalSequence,
    string FinalStatus,
    string BatchId,
    string BatchSha256,
    DateTimeOffset ArchivedAt);

/// <summary>The latest bounded journal capacity report from an authenticated adapter.</summary>
public sealed record AdapterCapacityReport(
    string SourceType,
    string SourceInstance,
    AgentCapacityCounter Counter,
    DateTimeOffset ObservedAt);

/// <summary>Durable authorization, delivery and bounded event sequence state. Online flags remain in memory.</summary>
public sealed record RelayState
{
    public RelayState(
        int schemaVersion = 2,
        IReadOnlyList<PendingRegistration>? pending = null,
        IReadOnlyList<RegistrationGrant>? grants = null,
        IReadOnlyList<QueuedInboundEvent>? inbound = null,
        IReadOnlyList<QueuedDispatch>? outbound = null,
        IReadOnlyList<DispatchReceipt>? dispatchReceipts = null,
        IReadOnlyList<string>? inboundEventKeys = null,
        IReadOnlyList<InboundEventWatermark>? inboundEventWatermarks = null,
        IReadOnlyList<RelayArchiveBatchState>? archiveBatches = null,
        IReadOnlyList<AgentArchiveTombstone>? archiveTombstones = null,
        IReadOnlyList<AdapterCapacityReport>? adapterCapacityReports = null)
    {
        SchemaVersion = schemaVersion;
        Pending = pending ?? Array.Empty<PendingRegistration>();
        Grants = grants ?? Array.Empty<RegistrationGrant>();
        Inbound = inbound ?? Array.Empty<QueuedInboundEvent>();
        Outbound = outbound ?? Array.Empty<QueuedDispatch>();
        DispatchReceipts = dispatchReceipts ?? Array.Empty<DispatchReceipt>();
        InboundEventKeys = inboundEventKeys ?? Array.Empty<string>();
        InboundEventWatermarks = inboundEventWatermarks ?? Array.Empty<InboundEventWatermark>();
        ArchiveBatches = archiveBatches ?? Array.Empty<RelayArchiveBatchState>();
        ArchiveTombstones = archiveTombstones ?? Array.Empty<AgentArchiveTombstone>();
        AdapterCapacityReports = adapterCapacityReports ?? Array.Empty<AdapterCapacityReport>();
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

    [JsonPropertyName("archive_batches")]
    public IReadOnlyList<RelayArchiveBatchState> ArchiveBatches { get; init; }

    [JsonPropertyName("archive_tombstones")]
    public IReadOnlyList<AgentArchiveTombstone> ArchiveTombstones { get; init; }

    [JsonPropertyName("adapter_capacity_reports")]
    public IReadOnlyList<AdapterCapacityReport> AdapterCapacityReports { get; init; }

    public RelayState ToCurrentSchema() => SchemaVersion == 1
        ? this with
        {
            SchemaVersion = 2,
            ArchiveBatches = ArchiveBatches ?? Array.Empty<RelayArchiveBatchState>(),
            ArchiveTombstones = ArchiveTombstones ?? Array.Empty<AgentArchiveTombstone>(),
            AdapterCapacityReports = AdapterCapacityReports ?? Array.Empty<AdapterCapacityReport>(),
        }
        : this;

    public static RelayState Empty { get; } = new();
}
