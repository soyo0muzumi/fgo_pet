using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.Services;

public sealed record AgentReconciliationResult(string Result, string? SafeError = null)
{
    public bool Applied => Result == "applied";
}

/// <summary>
/// Applies an explicitly confirmed local outcome to a dispatch whose transport
/// result was unknown. It never calls Relay or creates a new dispatch request.
/// A max-sequence local event prevents a late remote replay from reopening the
/// execution after the user has confirmed the observed state.
/// </summary>
public sealed class AgentReconciliationService
{
    private readonly IAgentRepository _agents;
    private readonly AgentEventProjector? _projector;
    private readonly TimeProvider _time;

    public AgentReconciliationService(
        IAgentRepository agents,
        TimeProvider time,
        AgentEventProjector? projector = null)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _projector = projector;
    }

    public Task<AgentReconciliationResult> ConfirmAsync(
        AgentTaskProjection projection,
        AgentExecutionStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        cancellationToken.ThrowIfCancellationRequested();
        if (status is not (AgentExecutionStatus.Active
            or AgentExecutionStatus.Completed
            or AgentExecutionStatus.Failed
            or AgentExecutionStatus.Cancelled))
        {
            return Task.FromResult(new AgentReconciliationResult("rejected", "reconciliation_status_invalid"));
        }

        var execution = _agents.GetExecution(projection.SourceType, projection.SourceInstance, projection.TaskId);
        if (execution is null)
        {
            return Task.FromResult(new AgentReconciliationResult("rejected", "execution_not_found"));
        }

        if (execution.Status != AgentExecutionStatus.DispatchOutcomeUnknown)
        {
            return Task.FromResult(new AgentReconciliationResult("already_applied"));
        }

        var eventType = status switch
        {
            AgentExecutionStatus.Active => AgentEventType.TaskResumed,
            AgentExecutionStatus.Completed => AgentEventType.TaskCompleted,
            AgentExecutionStatus.Failed => AgentEventType.TaskFailed,
            AgentExecutionStatus.Cancelled => AgentEventType.TaskCancelled,
            _ => throw new InvalidOperationException("Unsupported reconciliation status."),
        };
        var localEvent = new AgentEvent(
            execution.SourceType,
            execution.SourceInstance,
            execution.TaskId,
            long.MaxValue,
            eventType,
            _time.GetUtcNow(),
            summary: "用户已人工核对执行结果。",
            TodoId: execution.TodoId,
            DispatchRequestId: execution.DispatchRequestId,
            RemoteTaskId: execution.RemoteTaskId);

        if (_projector is not null)
        {
            var result = _projector.Apply(localEvent);
            return Task.FromResult(result is AgentProjectionApplyResult.Applied
                ? new AgentReconciliationResult("applied")
                : new AgentReconciliationResult("rejected", "reconciliation_not_applied"));
        }

        var persistence = _agents.ApplyEvent(localEvent);
        return Task.FromResult(persistence is AgentEventApplyResult.Applied or AgentEventApplyResult.AlreadyApplied
            ? new AgentReconciliationResult("applied")
            : new AgentReconciliationResult("rejected", "reconciliation_not_applied"));
    }
}
