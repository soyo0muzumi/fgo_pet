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

    private static class AgentExecutionFixture
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

        public static AgentExecution Dispatching() =>
            new("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", At);

        public static AgentExecution DispatchOutcomeUnknown() =>
            Dispatching().MarkDispatchOutcomeUnknown(At.AddMinutes(1));
    }
}
