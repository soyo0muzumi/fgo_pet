using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.Core.Tests.Agents;

public sealed class AgentExecutionTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

    [Fact]
    public void An_execution_moves_through_dispatch_active_attention_and_completion()
    {
        var execution = new AgentExecution(
            "execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", At)
            .MarkStarted(At.AddMinutes(1))
            .MarkAttention(At.AddMinutes(2))
            .MarkCompleted(At.AddMinutes(3));

        Assert.Equal(AgentExecutionStatus.Completed, execution.Status);
        Assert.True(execution.IsTerminal);
        Assert.Equal(At.AddMinutes(3), execution.EndedAt);
    }

    [Fact]
    public void A_terminal_execution_cannot_be_started_again()
    {
        var execution = new AgentExecution(
            "execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", At)
            .MarkCompleted(At.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => execution.MarkStarted(At.AddMinutes(2)));
    }

    [Fact]
    public void A_todo_cannot_have_two_non_terminal_executions()
    {
        var first = new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", At);
        var second = new AgentExecution("execution-2", "todo-1", "claude", "source-2", "task-2", "dispatch-2", At);

        Assert.Throws<InvalidOperationException>(() => AgentExecution.ValidateCanStart(new[] { first, second }));
    }

    [Fact]
    public void Failed_and_cancelled_executions_are_terminal_and_signal_replan()
    {
        var failed = new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", At)
            .MarkFailed(At.AddMinutes(1));
        var cancelled = new AgentExecution("execution-2", "todo-1", "codex", "source-1", "task-2", "dispatch-2", At)
            .MarkCancelled(At.AddMinutes(1));

        Assert.True(failed.IsTerminal);
        Assert.True(cancelled.IsTerminal);
        Assert.True(failed.ShouldReturnTodoToPlanned);
        Assert.True(cancelled.ShouldReturnTodoToPlanned);
    }
}
