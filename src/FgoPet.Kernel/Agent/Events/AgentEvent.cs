namespace FgoPet.Kernel.Agent;

public enum AgentEventKind
{
    RunCreated, RunStarted, StepStarted, StepCompleted, ModelStarted, ModelCompleted,
    ToolRequested, ToolStarted, ToolCompleted, ToolFailed, SkillLoaded,
    ApprovalRequested, ApprovalResolved, UserInputRequested, UserInputResolved,
    RunCompleted, RunFailed, RunCancelled, RunBudgetExceeded, RunInterrupted, RunExecutionUnknown
}

/// <summary>Metadata only. Correlations are host-generated hashes, never content or local paths.</summary>
public sealed record AgentEvent(long Sequence, DateTimeOffset Timestamp, AgentEventKind Kind,
    string RunCorrelation, int StepNumber, string? CallCorrelation = null, string? ErrorCode = null);
