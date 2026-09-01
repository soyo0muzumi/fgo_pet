using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.Core.Tests.Agents;

public sealed class AgentExecutionSafetyTests
{
    [Fact]
    public void MarkDispatchOutcomeUnknown_preserves_identity_and_is_non_terminal()
    {
        var execution = AgentExecutionFixture.Dispatching();

        var unknown = execution.MarkDispatchOutcomeUnknown(execution.UpdatedAt.AddMinutes(1));

        Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, unknown.Status);
        Assert.Equal(execution.DispatchRequestId, unknown.DispatchRequestId);
        Assert.False(unknown.IsTerminal);
        Assert.False(unknown.ShouldReturnTodoToPlanned);
    }

    [Fact]
    public void CreateNewAttempt_requires_terminal_or_explicitly_abandoned_previous_execution()
    {
        var unknown = AgentExecutionFixture.DispatchOutcomeUnknown();

        Assert.Throws<InvalidOperationException>(() =>
            AgentExecution.CreateAttemptAfter(unknown, "new-execution", "new-task", "new-request", unknown.UpdatedAt));
    }

    [Fact]
    public void CreateNewAttempt_after_terminal_execution_sets_link_and_preserves_source_and_todo_identity()
    {
        var previous = AgentExecutionFixture.Completed();

        var attempt = AgentExecution.CreateAttemptAfter(
            previous, "execution-2", "task-2", "dispatch-2", previous.UpdatedAt.AddMinutes(1));

        Assert.Equal(AgentExecutionStatus.Dispatching, attempt.Status);
        Assert.Equal(previous.Id, attempt.PreviousExecutionId);
        Assert.Equal(previous.TodoId, attempt.TodoId);
        Assert.Equal(previous.SourceType, attempt.SourceType);
        Assert.Equal(previous.SourceInstance, attempt.SourceInstance);
        Assert.NotEqual(previous.Id, attempt.Id);
        Assert.NotEqual(previous.TaskId, attempt.TaskId);
        Assert.NotEqual(previous.DispatchRequestId, attempt.DispatchRequestId);
    }

    [Fact]
    public void CreateNewAttempt_rejects_reused_execution_id()
    {
        var previous = AgentExecutionFixture.Completed();

        Assert.Throws<InvalidOperationException>(() => AgentExecution.CreateAttemptAfter(
            previous, previous.Id, "task-2", "dispatch-2", previous.UpdatedAt.AddMinutes(1)));
    }

    [Fact]
    public void CreateNewAttempt_rejects_reused_task_id()
    {
        var previous = AgentExecutionFixture.Completed();

        Assert.Throws<InvalidOperationException>(() => AgentExecution.CreateAttemptAfter(
            previous, "execution-2", previous.TaskId, "dispatch-2", previous.UpdatedAt.AddMinutes(1)));
    }

    [Fact]
    public void CreateNewAttempt_rejects_reused_dispatch_request_id()
    {
        var previous = AgentExecutionFixture.Completed();

        Assert.Throws<InvalidOperationException>(() => AgentExecution.CreateAttemptAfter(
            previous, "execution-2", "task-2", previous.DispatchRequestId, previous.UpdatedAt.AddMinutes(1)));
    }

    private static class AgentExecutionFixture
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

        public static AgentExecution Dispatching() =>
            new("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", At);

        public static AgentExecution DispatchOutcomeUnknown() =>
            Dispatching().MarkDispatchOutcomeUnknown(At.AddMinutes(1));

        public static AgentExecution Completed() =>
            Dispatching().MarkCompleted(At.AddMinutes(1));
    }
}
