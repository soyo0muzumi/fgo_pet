using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

/// <summary>A bounded provider-neutral capacity report.</summary>
public sealed record AgentCapacityCounter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("used")] int Used,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("archivable")] int Archivable);

/// <summary>The response returned for an application maintenance status request.</summary>
public sealed record AgentMaintenanceStatusResponse
{
    [JsonConstructor]
    public AgentMaintenanceStatusResponse(
        IReadOnlyList<AgentCapacityCounter> counters,
        DateTimeOffset? oldestArchivableAt,
        string? activeBatchId,
        string? safeError)
    {
        Counters = counters is null ? null! : counters.ToArray();
        OldestArchivableAt = oldestArchivableAt;
        ActiveBatchId = activeBatchId;
        SafeError = safeError;
    }

    [JsonPropertyName("counters")]
    public IReadOnlyList<AgentCapacityCounter> Counters { get; init; } = Array.Empty<AgentCapacityCounter>();

    [JsonPropertyName("oldest_archivable_at")]
    public DateTimeOffset? OldestArchivableAt { get; init; }

    [JsonPropertyName("active_batch_id")]
    public string? ActiveBatchId { get; init; }

    [JsonPropertyName("safe_error")]
    public string? SafeError { get; init; }
}

/// <summary>
/// A terminal execution identity sent as part of an archive prepare request.
/// This type deliberately carries only opaque identities, terminal status text,
/// timing, and a digest; it has no dependency on the Core execution model.
/// </summary>
public sealed record AgentArchiveProtocolItem
{
    [JsonConstructor]
    public AgentArchiveProtocolItem(
        string sourceType,
        string sourceInstance,
        string taskId,
        string dispatchRequestId,
        long finalSequence,
        string finalStatus,
        DateTimeOffset endedAt,
        string executionId,
        string summarySha256)
    {
        SourceType = sourceType;
        SourceInstance = sourceInstance;
        TaskId = taskId;
        DispatchRequestId = dispatchRequestId;
        FinalSequence = finalSequence;
        FinalStatus = finalStatus;
        EndedAt = endedAt;
        ExecutionId = executionId;
        SummarySha256 = summarySha256;
    }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("source_instance")]
    public string SourceInstance { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("dispatch_request_id")]
    public string DispatchRequestId { get; init; } = string.Empty;

    [JsonPropertyName("final_sequence")]
    public long FinalSequence { get; init; }

    [JsonPropertyName("final_status")]
    public string FinalStatus { get; init; } = string.Empty;

    [JsonPropertyName("ended_at")]
    public DateTimeOffset EndedAt { get; init; }

    [JsonPropertyName("execution_id")]
    public string ExecutionId { get; init; } = string.Empty;

    [JsonPropertyName("summary_sha256")]
    public string SummarySha256 { get; init; } = string.Empty;
}

/// <summary>A bounded batch of terminal identities to prepare for archiving.</summary>
public sealed record AgentArchivePrepareRequest
{
    [JsonConstructor]
    public AgentArchivePrepareRequest(
        string batchId,
        IReadOnlyList<AgentArchiveProtocolItem> items,
        string batchSha256)
    {
        BatchId = batchId;
        Items = items is null ? null! : items.ToArray();
        BatchSha256 = batchSha256;
    }

    [JsonPropertyName("batch_id")]
    public string BatchId { get; init; } = string.Empty;

    [JsonPropertyName("items")]
    public IReadOnlyList<AgentArchiveProtocolItem> Items { get; init; } = Array.Empty<AgentArchiveProtocolItem>();

    [JsonPropertyName("batch_sha256")]
    public string BatchSha256 { get; init; } = string.Empty;
}

/// <summary>A commit request for a previously prepared archive batch.</summary>
public sealed record AgentArchiveCommitRequest(
    [property: JsonPropertyName("batch_id")] string BatchId,
    [property: JsonPropertyName("batch_sha256")] string BatchSha256);

/// <summary>
/// Adapter maintenance heartbeat and optional acknowledgement. Acknowledged
/// batch and phase are either both absent or both present.
/// </summary>
public sealed record AdapterMaintenanceSyncRequest(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_instance")] string SourceInstance,
    [property: JsonPropertyName("acknowledged_batch_id")] string? AcknowledgedBatchId,
    [property: JsonPropertyName("acknowledged_phase")] string? AcknowledgedPhase,
    [property: JsonPropertyName("safe_error")] string? SafeError,
    [property: JsonPropertyName("adapter_journal")] AgentCapacityCounter AdapterJournal);
