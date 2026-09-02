using FgoPet.Core.Agents;

namespace FgoPet.Infrastructure.Agents;

public enum AgentProjectionApplyResult
{
    Applied,
    IgnoredDuplicate,
    IgnoredStale,
    Removed,
}

public sealed record AgentTaskProjection(
    string Identity,
    string SourceType,
    string SourceInstance,
    string TaskId,
    string? TodoId,
    AgentExecutionStatus Status,
    string? Summary,
    bool AttentionRequired,
    bool GoalCompleted,
    IReadOnlyList<string> CoveredTaskKeys,
    long LastSequence,
    DateTimeOffset UpdatedAt,
    string? RemoteTaskId = null,
    string? ExecutionId = null);

public sealed class AgentEventProjector
{
    private readonly IAgentRepository? _agents;
    private readonly Dictionary<string, AgentTaskProjection> _projections = new(StringComparer.Ordinal);

    public AgentEventProjector(IAgentRepository? agents = null) => _agents = agents;

    public IReadOnlyList<AgentTaskProjection> Current => _projections.Values
        .OrderByDescending(item => item.UpdatedAt)
        .ToArray();

    public event Action<AgentEvent, AgentProjectionApplyResult>? EventApplied;
    public event Action<AgentExecution>? ExecutionRestored;

    public AgentTaskProjection? Get(string identity) => _projections.GetValueOrDefault(identity);

    /// <summary>
    /// Rehydrates a non-terminal execution after an App restart. The persisted
    /// execution is authoritative when the Relay has no new receipt to replay;
    /// subsequent events still start at sequence one and advance this projection.
    /// </summary>
    public void Restore(AgentExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var identity = $"{execution.SourceType}/{execution.SourceInstance}/{execution.TaskId}";
        if (execution.IsTerminal || _projections.ContainsKey(identity))
        {
            return;
        }

        _projections[identity] = new AgentTaskProjection(
            identity,
            execution.SourceType,
            execution.SourceInstance,
            execution.TaskId,
            execution.TodoId,
            execution.Status,
            Summary: null,
            AttentionRequired: execution.Status == AgentExecutionStatus.Attention,
            GoalCompleted: false,
            CoveredTaskKeys: Array.Empty<string>(),
            LastSequence: 0,
            UpdatedAt: execution.UpdatedAt,
            RemoteTaskId: execution.RemoteTaskId,
            ExecutionId: execution.Id);
        ExecutionRestored?.Invoke(execution);
    }

    /// <summary>Refreshes a persisted non-terminal execution after a local boundary update.</summary>
    public void Synchronize(AgentExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.IsTerminal) return;
        var identity = $"{execution.SourceType}/{execution.SourceInstance}/{execution.TaskId}";
        if (_projections.TryGetValue(identity, out var existing))
        {
            _projections[identity] = existing with
            {
                TodoId = execution.TodoId,
                Status = execution.Status,
                AttentionRequired = execution.Status == AgentExecutionStatus.Attention,
                UpdatedAt = execution.UpdatedAt,
                RemoteTaskId = execution.RemoteTaskId,
                ExecutionId = execution.Id,
            };
        }
        else
        {
            Restore(execution);
            return;
        }

        ExecutionRestored?.Invoke(execution);
    }

    public AgentProjectionApplyResult Apply(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        AgentEventApplyResult? persistenceResult;
        try
        {
            persistenceResult = _agents?.ApplyEvent(agentEvent);
        }
        catch (KeyNotFoundException) when (_agents is not null)
        {
            // Hooks may observe an Agent task that was not dispatched by FGO Pet.
            // Keep that task in the in-memory companion projection without creating
            // a Todo or execution record from untrusted external state.
            persistenceResult = null;
        }
        if (persistenceResult == AgentEventApplyResult.AlreadyApplied)
        {
            return AgentProjectionApplyResult.IgnoredDuplicate;
        }

        if (persistenceResult == AgentEventApplyResult.IgnoredStale)
        {
            return AgentProjectionApplyResult.IgnoredStale;
        }

        var identity = agentEvent.TaskIdentity;
        if (_projections.TryGetValue(identity, out var existing))
        {
            if (agentEvent.Sequence < existing.LastSequence) return AgentProjectionApplyResult.IgnoredStale;
            if (agentEvent.Sequence == existing.LastSequence) return AgentProjectionApplyResult.IgnoredDuplicate;
            if (existing.Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Failed or AgentExecutionStatus.Cancelled)
            {
                return AgentProjectionApplyResult.IgnoredStale;
            }
        }

        if (agentEvent.EventType == AgentEventType.TaskRemoved)
        {
            _projections.Remove(identity);
            EventApplied?.Invoke(agentEvent, AgentProjectionApplyResult.Removed);
            return AgentProjectionApplyResult.Removed;
        }

        var status = existing?.Status ?? AgentExecutionStatus.Dispatching;
        var attention = existing?.AttentionRequired ?? false;
        var goalCompleted = existing?.GoalCompleted ?? false;
        var coveredTaskKeys = agentEvent.CoveredTaskKeys.Count == 0
            ? existing?.CoveredTaskKeys ?? Array.Empty<string>()
            : agentEvent.CoveredTaskKeys;
        var summary = agentEvent.Summary ?? existing?.Summary;
        switch (agentEvent.EventType)
        {
            case AgentEventType.TaskStarted:
            case AgentEventType.TaskResumed:
                status = AgentExecutionStatus.Active;
                attention = false;
                break;
            case AgentEventType.AttentionRequired:
                status = AgentExecutionStatus.Attention;
                attention = true;
                break;
            case AgentEventType.TaskCompleted:
                status = AgentExecutionStatus.Completed;
                attention = false;
                break;
            case AgentEventType.TaskFailed:
                status = AgentExecutionStatus.Failed;
                attention = false;
                break;
            case AgentEventType.TaskCancelled:
                status = AgentExecutionStatus.Cancelled;
                attention = false;
                break;
            case AgentEventType.GoalCompleted:
                goalCompleted = true;
                attention = false;
                break;
        }

        _projections[identity] = new AgentTaskProjection(
            identity,
            agentEvent.SourceType,
            agentEvent.SourceInstance,
            agentEvent.TaskId,
            agentEvent.TodoId ?? existing?.TodoId,
            status,
            summary,
            attention,
            goalCompleted,
            coveredTaskKeys,
            agentEvent.Sequence,
            agentEvent.OccurredAt,
            agentEvent.RemoteTaskId ?? existing?.RemoteTaskId,
            existing?.ExecutionId);
        EventApplied?.Invoke(agentEvent, AgentProjectionApplyResult.Applied);
        return AgentProjectionApplyResult.Applied;
    }
}
