using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class AgentReconciliationServiceTests
{
    [Fact]
    public async Task Explicit_confirmation_updates_only_local_projection_and_blocks_late_replay()
    {
        var at = DateTimeOffset.Parse("2026-09-02T08:00:00Z");
        var execution = new AgentExecution(
            "execution-1", "todo-1", "codex", "instance-1", "task-1", "dispatch-1", at,
            AgentExecutionStatus.DispatchOutcomeUnknown);
        var repository = new FakeAgentRepository(execution);
        var projector = new AgentEventProjector(repository);
        projector.Restore(execution);
        var service = new AgentReconciliationService(repository, new FixedTimeProvider(at), projector);

        var result = await service.ConfirmAsync(
            projector.Get("codex/instance-1/task-1")!, AgentExecutionStatus.Completed);

        Assert.True(result.Applied);
        Assert.Equal(AgentExecutionStatus.Completed, repository.Execution!.Status);
        Assert.Equal(AgentExecutionStatus.Completed, projector.Get("codex/instance-1/task-1")!.Status);
        Assert.Equal(long.MaxValue, repository.LastEvent!.Sequence);
        Assert.Null(repository.RelayCalls);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAgentRepository(AgentExecution initial) : IAgentRepository
    {
        public AgentExecution? Execution { get; private set; } = initial;
        public AgentEvent? LastEvent { get; private set; }
        public object? RelayCalls { get; private set; }
        public void SaveExecution(AgentExecution execution) => Execution = execution;
        public AgentExecution? GetExecution(string id) => Execution?.Id == id ? Execution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) =>
            Execution is { } value && value.SourceType == sourceType && value.SourceInstance == sourceInstance && value.TaskId == taskId ? value : null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => Execution is { IsTerminal: false } value ? new[] { value } : Array.Empty<AgentExecution>();
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => Array.Empty<AgentExecution>();
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public long GetLatestEventSequence(string sourceType, string sourceInstance, string taskId) => 0;
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => Array.Empty<AgentArchiveBatch>();
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent)
        {
            LastEvent = agentEvent;
            if (Execution is null) return AgentEventApplyResult.IgnoredStale;
            Execution = agentEvent.EventType switch
            {
                AgentEventType.TaskResumed => Execution.MarkResumed(agentEvent.OccurredAt),
                AgentEventType.TaskCompleted => Execution.MarkCompleted(agentEvent.OccurredAt),
                AgentEventType.TaskFailed => Execution.MarkFailed(agentEvent.OccurredAt),
                AgentEventType.TaskCancelled => Execution.MarkCancelled(agentEvent.OccurredAt),
                _ => Execution,
            };
            return AgentEventApplyResult.Applied;
        }
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }
}
