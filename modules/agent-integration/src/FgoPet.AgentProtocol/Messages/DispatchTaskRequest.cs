using System.Text.Json.Serialization;

namespace FgoPet.AgentProtocol.Messages;

public sealed record DispatchTaskRequest
{
    public DispatchTaskRequest()
    {
    }

    public DispatchTaskRequest(
        string dispatchRequestId,
        string todoId,
        string title,
        string? description,
        string priority,
        DateTimeOffset? dueAt,
        string targetId)
    {
        DispatchRequestId = dispatchRequestId;
        TodoId = todoId;
        Title = title;
        Description = description;
        Priority = priority;
        DueAt = dueAt;
        TargetId = targetId;
    }

    [JsonPropertyName("dispatch_request_id")]
    public string DispatchRequestId { get; init; } = string.Empty;

    [JsonPropertyName("todo_id")]
    public string TodoId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";

    [JsonPropertyName("due_at")]
    public DateTimeOffset? DueAt { get; init; }

    [JsonPropertyName("target_id")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("source_instance_id")]
    public string? SourceInstanceId { get; init; }
}
