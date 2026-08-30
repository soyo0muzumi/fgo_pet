using FgoPet.App.ViewModels;
using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.App.Tests.ViewModels;

public sealed class AgentCurrentTaskViewModelTests
{
    [Fact]
    public void Shows_the_latest_active_task_and_attention_without_touching_focus_state()
    {
        var viewModel = new AgentCurrentTaskViewModel(new AgentEventProjector(), TimeProvider.System);
        var started = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, DateTimeOffset.UtcNow, summary: "Building the bridge");

        viewModel.Apply(started);
        Assert.Equal("task-1", viewModel.CurrentTaskId);
        Assert.Equal("Building the bridge", viewModel.CurrentTaskText);
        Assert.False(viewModel.AttentionRequired);

        viewModel.Apply(new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.AttentionRequired, DateTimeOffset.UtcNow));
        Assert.True(viewModel.AttentionRequired);
        Assert.Contains("确认", viewModel.AttentionText, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_events_do_not_replay_attention_and_goal_sets_a_consumable_talk_intent()
    {
        var viewModel = new AgentCurrentTaskViewModel(new AgentEventProjector(), TimeProvider.System);
        var attention = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.AttentionRequired, DateTimeOffset.UtcNow);

        viewModel.Apply(attention);
        viewModel.Apply(attention);
        Assert.True(viewModel.AttentionRequired);

        viewModel.Apply(new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.GoalCompleted, DateTimeOffset.UtcNow));
        Assert.True(viewModel.WantsToTalk);
        Assert.True(viewModel.ConsumeTalkIntent());
        Assert.False(viewModel.WantsToTalk);
    }
}
