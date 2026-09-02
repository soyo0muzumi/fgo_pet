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
        Assert.False(viewModel.HasOtherActiveTasks);

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

    [Fact]
    public void Shows_other_active_task_count_without_an_arbitrary_three_item_cap()
    {
        var viewModel = new AgentCurrentTaskViewModel(new AgentEventProjector(), TimeProvider.System);
        for (var index = 1; index <= 5; index++)
        {
            viewModel.Apply(new AgentEvent("codex", "source-1", $"task-{index}", 1, AgentEventType.TaskStarted, DateTimeOffset.UtcNow));
        }

        Assert.Equal(4, viewModel.OtherActiveCount);
        Assert.True(viewModel.HasOtherActiveTasks);
    }

    [Fact]
    public void Unknown_dispatch_is_visible_for_reconciliation_and_opening_the_original_task()
    {
        var execution = new AgentExecution(
            "execution-1", "todo-1", "codex", "instance-1", "task-1", "dispatch-1",
            DateTimeOffset.UtcNow, AgentExecutionStatus.DispatchOutcomeUnknown);
        var projector = new AgentEventProjector();
        projector.Restore(execution);
        var viewModel = new AgentCurrentTaskViewModel(projector, TimeProvider.System);

        Assert.True(viewModel.OutcomeUnknown);
        Assert.True(viewModel.AttentionRequired);
        Assert.Contains("待核对", viewModel.AttentionText, StringComparison.Ordinal);
        Assert.Equal("task-1", viewModel.CurrentTaskId);
    }
}
