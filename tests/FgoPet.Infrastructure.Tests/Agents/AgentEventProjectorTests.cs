using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Agents;

public sealed class AgentEventProjectorTests
{
    [Fact]
    public void Projection_maps_attention_and_clears_it_on_resume()
    {
        var projector = new AgentEventProjector();
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

        projector.Apply(new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at, TodoId: "todo-1"));
        projector.Apply(new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.AttentionRequired, at.AddMinutes(1), TodoId: "todo-1", RemoteTaskId: "thread-1"));
        Assert.True(projector.Current.Single().AttentionRequired);
        Assert.Equal("thread-1", projector.Current.Single().RemoteTaskId);

        projector.Apply(new AgentEvent("codex", "source-1", "task-1", 3, AgentEventType.TaskResumed, at.AddMinutes(2), TodoId: "todo-1"));
        var current = Assert.Single(projector.Current);
        Assert.False(current.AttentionRequired);
        Assert.Equal(AgentExecutionStatus.Active, current.Status);
    }

    [Fact]
    public void Duplicate_or_late_events_never_regress_a_terminal_projection()
    {
        var projector = new AgentEventProjector();
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        projector.Apply(new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.TaskCompleted, at.AddMinutes(2)));

        Assert.Equal(AgentProjectionApplyResult.IgnoredStale,
            projector.Apply(new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at)));
        Assert.Equal(AgentExecutionStatus.Completed, Assert.Single(projector.Current).Status);
    }
}
