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
    long LastSequence,
    DateTimeOffset UpdatedAt);

public sealed class AgentEventProjector
{
    private readonly IAgentRepository? _agents;
    private readonly Dictionary<string, AgentTaskProjection> _projections = new(StringComparer.Ordinal);

    public AgentEventProjector(IAgentRepository? agents = null) => _agents = agents;

    public IReadOnlyList<AgentTaskProjection> Current => _projections.Values
        .OrderByDescending(item => item.UpdatedAt)
        .ToArray();

    public AgentTaskProjection? Get(string identity) => _projections.GetValueOrDefault(identity);

    public AgentProjectionApplyResult Apply(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        var persistenceResult = _agents?.ApplyEvent(agentEvent);
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
            return AgentProjectionApplyResult.Removed;
        }

        var status = existing?.Status ?? AgentExecutionStatus.Dispatching;
        var attention = existing?.AttentionRequired ?? false;
        var goalCompleted = existing?.GoalCompleted ?? false;
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
            agentEvent.Sequence,
            agentEvent.OccurredAt);
        return AgentProjectionApplyResult.Applied;
    }
}
