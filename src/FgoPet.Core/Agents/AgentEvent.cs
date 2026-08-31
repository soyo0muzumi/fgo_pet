namespace FgoPet.Core.Agents;

public sealed record AgentEvent
{
    public AgentEvent(
        string sourceType,
        string sourceInstance,
        string taskId,
        long sequence,
        AgentEventType eventType,
        DateTimeOffset occurredAt,
        string? title = null,
        string? summary = null,
        bool IsPrivate = false,
        string? TodoId = null,
        string? DispatchRequestId = null,
        IReadOnlyList<string>? coveredTaskKeys = null)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        SourceType = AgentIdentityValidation.Id(sourceType, nameof(sourceType));
        SourceInstance = AgentIdentityValidation.Id(sourceInstance, nameof(sourceInstance));
        TaskId = AgentIdentityValidation.Id(taskId, nameof(taskId));
        Sequence = sequence;
        EventType = eventType;
        OccurredAt = occurredAt;
        Title = IsPrivate ? null : NormalizeOptional(title, nameof(title), 500);
        Summary = IsPrivate ? null : NormalizeOptional(summary, nameof(summary), 4_000);
        this.IsPrivate = IsPrivate;
        this.TodoId = string.IsNullOrWhiteSpace(TodoId) ? null : AgentIdentityValidation.Id(TodoId, nameof(TodoId));
        this.DispatchRequestId = string.IsNullOrWhiteSpace(DispatchRequestId)
            ? null
            : AgentIdentityValidation.Id(DispatchRequestId, nameof(DispatchRequestId));
        CoveredTaskKeys = coveredTaskKeys?.ToArray() ?? Array.Empty<string>();
    }

    public string SourceType { get; init; }
    public string SourceInstance { get; init; }
    public string TaskId { get; init; }
    public long Sequence { get; init; }
    public AgentEventType EventType { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public bool IsPrivate { get; init; }
    public string? TodoId { get; init; }
    public string? DispatchRequestId { get; init; }
    public IReadOnlyList<string> CoveredTaskKeys { get; init; }
    public string Identity => $"{SourceType}/{SourceInstance}/{TaskId}/{Sequence}";
    public string TaskIdentity => $"{SourceType}/{SourceInstance}/{TaskId}";
    public string EventKey => Identity;
    public DateTimeOffset OccurredAtUtc => OccurredAt;

    private static string? NormalizeOptional(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return AgentIdentityValidation.Id(value, parameterName, maxLength);
    }
}
