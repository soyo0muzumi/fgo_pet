namespace FgoPet.Core.Agents;

public enum AgentEventType
{
    TaskDiscovered,
    TaskStarted,
    TaskUpdated,
    AttentionRequired,
    TaskResumed,
    MilestoneReached,
    TaskCompleted,
    TaskFailed,
    TaskCancelled,
    TaskRemoved,
    GoalCompleted,
}
