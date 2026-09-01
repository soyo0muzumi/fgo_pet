namespace FgoPet.Core.Agents;

public enum AgentExecutionStatus
{
    Dispatching,
    Active,
    Attention,
    DispatchOutcomeUnknown,
    Completed,
    Failed,
    Cancelled,
}

public sealed record AgentExecution
{
    public AgentExecution(
        string id,
        string todoId,
        string sourceType,
        string sourceInstance,
        string taskId,
        string dispatchRequestId,
        DateTimeOffset updatedAt,
        AgentExecutionStatus status = AgentExecutionStatus.Dispatching,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        string? previousExecutionId = null)
    {
        Id = AgentIdentityValidation.Id(id, nameof(id));
        TodoId = AgentIdentityValidation.Id(todoId, nameof(todoId));
        SourceType = AgentIdentityValidation.Id(sourceType, nameof(sourceType));
        SourceInstance = AgentIdentityValidation.Id(sourceInstance, nameof(sourceInstance));
        TaskId = AgentIdentityValidation.Id(taskId, nameof(taskId));
        DispatchRequestId = AgentIdentityValidation.Id(dispatchRequestId, nameof(dispatchRequestId));
        PreviousExecutionId = string.IsNullOrWhiteSpace(previousExecutionId)
            ? null
            : AgentIdentityValidation.Id(previousExecutionId, nameof(previousExecutionId));
        Status = status;
        StartedAt = startedAt;
        UpdatedAt = updatedAt;
        EndedAt = endedAt;

        if (status == AgentExecutionStatus.Completed && endedAt is null)
        {
            throw new ArgumentException("Completed executions require endedAt.", nameof(endedAt));
        }

        if (status is AgentExecutionStatus.Failed or AgentExecutionStatus.Cancelled && endedAt is null)
        {
            throw new ArgumentException("Terminal executions require endedAt.", nameof(endedAt));
        }
    }

    public string Id { get; }
    public string TodoId { get; }
    public string SourceType { get; }
    public string SourceInstance { get; }
    public string TaskId { get; }
    public string DispatchRequestId { get; }
    public string? PreviousExecutionId { get; }
    public AgentExecutionStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public bool IsTerminal => Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Failed or AgentExecutionStatus.Cancelled;
    public bool IsNonTerminal => !IsTerminal;
    public bool ShouldReturnTodoToPlanned => Status is AgentExecutionStatus.Failed or AgentExecutionStatus.Cancelled;

    public AgentExecution MarkStarted(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.Active, StartedAt = StartedAt ?? at, UpdatedAt = at };
    }

    public AgentExecution MarkResumed(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.Active, StartedAt = StartedAt ?? at, UpdatedAt = at };
    }

    public AgentExecution MarkAttention(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.Attention, StartedAt = StartedAt ?? at, UpdatedAt = at };
    }

    public AgentExecution MarkDispatchOutcomeUnknown(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.DispatchOutcomeUnknown, UpdatedAt = at };
    }

    public AgentExecution MarkUpdated(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { UpdatedAt = at };
    }

    public AgentExecution MarkCompleted(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.Completed, UpdatedAt = at, EndedAt = at };
    }

    public AgentExecution MarkFailed(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.Failed, UpdatedAt = at, EndedAt = at };
    }

    public AgentExecution MarkCancelled(DateTimeOffset at)
    {
        EnsureMutable();
        return this with { Status = AgentExecutionStatus.Cancelled, UpdatedAt = at, EndedAt = at };
    }

    public static AgentExecution CreateAttemptAfter(
        AgentExecution previous,
        string executionId,
        string taskId,
        string dispatchRequestId,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!previous.IsTerminal)
        {
            throw new InvalidOperationException("A new Agent execution requires a terminal previous execution.");
        }

        return new AgentExecution(
            executionId,
            previous.TodoId,
            previous.SourceType,
            previous.SourceInstance,
            taskId,
            dispatchRequestId,
            updatedAt,
            previousExecutionId: previous.Id);
    }

    public static void ValidateCanStart(IEnumerable<AgentExecution> executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        if (executions.Any(execution => execution.IsNonTerminal))
        {
            throw new InvalidOperationException("A Todo item already has a non-terminal Agent execution.");
        }
    }

    private void EnsureMutable()
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal Agent execution cannot change state.");
        }
    }
}

internal static class AgentIdentityValidation
{
    public static string Id(string value, string parameterName, int maxLength = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }
}
