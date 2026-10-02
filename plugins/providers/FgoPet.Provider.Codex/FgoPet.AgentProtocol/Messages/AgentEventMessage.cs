using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

public sealed record AgentEventMessage
{
    public AgentEventMessage()
    {
    }

    public AgentEventMessage(
        string sourceType,
        string sourceInstance,
        string taskId,
        long sequence,
        string eventType,
        DateTimeOffset occurredAt,
        string? Title = null,
        string? Summary = null,
        bool IsPrivate = false,
        string? TodoId = null,
        string? DispatchRequestId = null,
        IReadOnlyList<string>? CoveredTaskKeys = null,
        string? RemoteTaskId = null)
    {
        SourceType = sourceType;
        SourceInstance = sourceInstance;
        TaskId = taskId;
        Sequence = sequence;
        EventType = eventType;
        OccurredAt = occurredAt;
        this.Title = Title;
        this.Summary = Summary;
        this.IsPrivate = IsPrivate;
        this.TodoId = TodoId;
        this.DispatchRequestId = DispatchRequestId;
        this.CoveredTaskKeys = CoveredTaskKeys ?? Array.Empty<string>();
        this.RemoteTaskId = RemoteTaskId;
    }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("source_instance")]
    public string SourceInstance { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("is_private")]
    public bool IsPrivate { get; init; }

    [JsonPropertyName("todo_id")]
    public string? TodoId { get; init; }

    [JsonPropertyName("dispatch_request_id")]
    public string? DispatchRequestId { get; init; }

    [JsonPropertyName("remote_task_id")]
    public string? RemoteTaskId { get; init; }

    [JsonPropertyName("covered_task_keys")]
    public IReadOnlyList<string> CoveredTaskKeys { get; init; } = Array.Empty<string>();
}

public static class AgentEventWireNames
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["task_discovered"] = "task_discovered",
        ["task_started"] = "task_started",
        ["task_updated"] = "task_updated",
        ["attention_required"] = "attention_required",
        ["task_resumed"] = "task_resumed",
        ["milestone_reached"] = "milestone_reached",
        ["task_completed"] = "task_completed",
        ["task_failed"] = "task_failed",
        ["task_cancelled"] = "task_cancelled",
        ["task_removed"] = "task_removed",
        ["goal_completed"] = "goal_completed",
    };

    public static bool IsKnown(string? value) => value is not null && Names.ContainsKey(value);
}
