using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public enum AgentRunStatus
{
    Created, Running, WaitingUserInput, WaitingApproval, Completed, Failed,
    Cancelled, BudgetExceeded, Interrupted, ExecutionUnknown
}
public enum AgentCallStatus { Requested, Started, Completed, Denied, Failed, NotExecuted, ExecutionUnknown }
public enum AgentWaitKind { UserInput, Approval }
public enum ToolExecutionOutcomeKind { Completed, WaitingApproval, WaitingUserInput, Cancelled, ExecutionUnknown }

public sealed record AgentRunIdentity(string RunId, string RootUserMessageId, ToolScope Scope,
    string ModelRevision, long AuthorizationRevision);

public sealed record AgentRunSnapshot
{
    public required AgentRunIdentity Identity { get; init; }
    public RunBudget Budget { get; init; } = new();
    public AgentRunStatus Status { get; init; }
    public long Revision { get; init; }
    public int ModelRequests { get; init; }
    public int ToolCalls { get; init; }
    public int LoadedSkills { get; init; }
    public int StepNumber { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
    public bool IsTerminal => Status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled
        or AgentRunStatus.BudgetExceeded or AgentRunStatus.Interrupted or AgentRunStatus.ExecutionUnknown;
}

public sealed record AgentCallCheckpoint(ModelToolCall Call, AgentCallStatus Status = AgentCallStatus.Requested,
    bool Charged = false, ToolEffect? Effect = null, ToolResult? Result = null)
{
    public ApprovalRequest? Approval { get; init; }
}

public sealed record ActiveSkillMetadata(string Id, string PluginId, string Version, string ContentDigest);
public sealed record AgentCompletedStep(int StepNumber, ImmutableArray<AgentCallCheckpoint> Calls);

public sealed record AgentWaitState(string RequestId, AgentWaitKind Kind, int StepNumber,
    string CallId, long Revision, DateTimeOffset ExpiresAt);

public sealed record AgentRunCheckpoint
{
    public int SchemaVersion { get; init; } = 1;
    public required AgentRunSnapshot Snapshot { get; init; }
    public long JournalSequence { get; init; }
    public ImmutableArray<AgentCallCheckpoint> Calls { get; init; } = [];
    public int NextCallIndex { get; init; }
    public AgentWaitState? Waiting { get; init; }
    public string? FinalDeliveryId { get; init; }
    public string? FinalText { get; init; }
    public ImmutableArray<ActiveSkillMetadata> ActiveSkills { get; init; } = [];
    public ApprovalRequest? PendingApproval { get; init; }
    public UserInputRequest? PendingInput { get; init; }
    public ImmutableArray<ResolvedUserInput> ResolvedInputs { get; init; } = [];
    public ImmutableArray<AgentCompletedStep> CompletedSteps { get; init; } = [];
}

public sealed record AgentRunRequest(AgentRunIdentity Identity, RunBudget Budget,
    ImmutableArray<ModelMessage> InitialMessages, ProtectedQueryReference? Query = null);

public sealed record ResolvedUserInput(UserInputRequest Request, UserInputReply Reply);

public sealed record ToolExecutionRequest(AgentRunIdentity Identity, int StepNumber, ModelToolCall Call)
{
    internal long CheckpointRevision { get; init; }
    internal ApprovalRequest? Approval { get; init; }
}

public sealed record ToolExecutionOutcome(ToolExecutionOutcomeKind Kind, ToolResult? Result = null,
    AgentWaitState? Waiting = null, string? ErrorCode = null)
{
    public ApprovalRequest? ApprovalRequest { get; init; }
    public UserInputRequest? UserInputRequest { get; init; }
}

public interface IAgentRunStore
{
    ValueTask<AgentRunCheckpoint?> LoadAsync(string runId, CancellationToken token);
    ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token);
    ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expectedRevision,
        ImmutableArray<AgentEvent> events, CancellationToken token);
}
public interface IModelRequestBudget { ValueTask ReserveAsync(CancellationToken token); }
public interface IAgentRunFence { void EnsureCurrent(AgentRunIdentity identity, CancellationToken token); }
public interface IToolExecutionIntent { ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token); }
public interface IToolExecutionPipeline
{
    ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request, IToolExecutionIntent intent,
        CancellationToken token);
}
