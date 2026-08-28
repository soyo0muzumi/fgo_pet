using FgoPet.Core.Panels;
using Xunit;

namespace FgoPet.Core.Tests.Panels;

public sealed class AttachedPanelStateMachineTests
{
    [Theory]
    [InlineData(AttachedPanelState.Collapsed, PanelAction.PortraitClick, AttachedPanelState.Compact)]
    [InlineData(AttachedPanelState.Compact, PanelAction.DialogueClick, AttachedPanelState.ExpandedDialogue)]
    [InlineData(AttachedPanelState.Compact, PanelAction.TodoClick, AttachedPanelState.ExpandedTodo)]
    [InlineData(AttachedPanelState.Compact, PanelAction.Escape, AttachedPanelState.Collapsed)]
    [InlineData(AttachedPanelState.ExpandedDialogue, PanelAction.DialogueClick, AttachedPanelState.Compact)]
    [InlineData(AttachedPanelState.ExpandedDialogue, PanelAction.TodoClick, AttachedPanelState.ExpandedTodo)]
    [InlineData(AttachedPanelState.ExpandedDialogue, PanelAction.Escape, AttachedPanelState.Compact)]
    [InlineData(AttachedPanelState.ExpandedTodo, PanelAction.DialogueClick, AttachedPanelState.ExpandedDialogue)]
    [InlineData(AttachedPanelState.ExpandedTodo, PanelAction.TodoClick, AttachedPanelState.Compact)]
    [InlineData(AttachedPanelState.ExpandedTodo, PanelAction.Escape, AttachedPanelState.Compact)]
    public void Transition_is_deterministic(AttachedPanelState from, PanelAction action, AttachedPanelState expected) =>
        Assert.Equal(expected, AttachedPanelStateMachine.Transition(from, action));

    [Theory]
    [InlineData(AttachedPanelState.Compact, PanelAction.FocusClick, AttachedPanelState.ExpandedFocus)]
    [InlineData(AttachedPanelState.ExpandedFocus, PanelAction.FocusClick, AttachedPanelState.Compact)]
    [InlineData(AttachedPanelState.ExpandedFocus, PanelAction.TodayClick, AttachedPanelState.ExpandedToday)]
    [InlineData(AttachedPanelState.ExpandedToday, PanelAction.FocusClick, AttachedPanelState.ExpandedFocus)]
    [InlineData(AttachedPanelState.ExpandedToday, PanelAction.TodayClick, AttachedPanelState.Compact)]
    [InlineData(AttachedPanelState.ExpandedToday, PanelAction.TodoClick, AttachedPanelState.ExpandedTodo)]
    [InlineData(AttachedPanelState.ExpandedTodo, PanelAction.TodayClick, AttachedPanelState.ExpandedToday)]
    [InlineData(AttachedPanelState.ExpandedDialogue, PanelAction.TodayClick, AttachedPanelState.ExpandedToday)]
    [InlineData(AttachedPanelState.ExpandedToday, PanelAction.DialogueClick, AttachedPanelState.ExpandedDialogue)]
    [InlineData(AttachedPanelState.Compact, PanelAction.TodayClick, AttachedPanelState.ExpandedToday)]
    [InlineData(AttachedPanelState.ExpandedToday, PanelAction.Escape, AttachedPanelState.Compact)]
    public void Four_column_transitions_stretch_or_switch(AttachedPanelState from, PanelAction action, AttachedPanelState expected) =>
        Assert.Equal(expected, AttachedPanelStateMachine.Transition(from, action));

    [Theory]
    [InlineData(AttachedPanelState.ExpandedFocus)]
    [InlineData(AttachedPanelState.ExpandedToday)]
    [InlineData(AttachedPanelState.ExpandedTodo)]
    [InlineData(AttachedPanelState.ExpandedDialogue)]
    public void PortraitClick_from_any_expanded_state_returns_collapsed(AttachedPanelState from) =>
        Assert.Equal(AttachedPanelState.Collapsed, AttachedPanelStateMachine.Transition(from, PanelAction.PortraitClick));

    [Theory]
    [InlineData(AttachedPanelState.ExpandedFocus)]
    [InlineData(AttachedPanelState.ExpandedToday)]
    [InlineData(AttachedPanelState.ExpandedTodo)]
    [InlineData(AttachedPanelState.ExpandedDialogue)]
    public void Escape_from_any_expanded_state_returns_compact(AttachedPanelState from) =>
        Assert.Equal(AttachedPanelState.Compact, AttachedPanelStateMachine.Transition(from, PanelAction.Escape));

    [Fact]
    public void An_inapplicable_action_is_a_no_op()
    {
        Assert.Equal(AttachedPanelState.Collapsed, AttachedPanelStateMachine.Transition(AttachedPanelState.Collapsed, PanelAction.Escape));
        Assert.Equal(AttachedPanelState.Collapsed, AttachedPanelStateMachine.Transition(AttachedPanelState.Collapsed, PanelAction.TodoClick));
        Assert.Equal(AttachedPanelState.Collapsed, AttachedPanelStateMachine.Transition(AttachedPanelState.Collapsed, PanelAction.DialogueClick));
    }

    [Theory]
    [InlineData(AttachedPanelState.ExpandedDialogue)]
    [InlineData(AttachedPanelState.ExpandedTodo)]
    [InlineData(AttachedPanelState.ExpandedFocus)]
    [InlineData(AttachedPanelState.ExpandedToday)]
    public void Idle_collapses_an_expanded_state_when_the_pointer_is_outside(AttachedPanelState from)
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00Z"));

        var result = AttachedPanelStateMachine.ApplyIdle(
            from,
            time,
            lastInteraction: DateTimeOffset.Parse("2026-08-27T08:59:00Z"),
            idleTimeout: TimeSpan.FromSeconds(30),
            autoCollapseEnabled: true,
            pointerOutside: true);

        Assert.Equal(AttachedPanelState.Compact, result);
    }

    [Fact]
    public void Idle_does_not_collapse_within_the_timeout()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00Z"));

        var result = AttachedPanelStateMachine.ApplyIdle(
            AttachedPanelState.ExpandedTodo,
            time,
            lastInteraction: DateTimeOffset.Parse("2026-08-27T08:59:59Z"),
            idleTimeout: TimeSpan.FromSeconds(30),
            autoCollapseEnabled: true,
            pointerOutside: true);

        Assert.Equal(AttachedPanelState.ExpandedTodo, result);
    }

    [Fact]
    public void Idle_does_not_collapse_when_the_pointer_is_inside()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00Z"));

        var result = AttachedPanelStateMachine.ApplyIdle(
            AttachedPanelState.ExpandedDialogue,
            time,
            lastInteraction: DateTimeOffset.Parse("2026-08-27T08:00:00Z"),
            idleTimeout: TimeSpan.FromSeconds(30),
            autoCollapseEnabled: true,
            pointerOutside: false);

        Assert.Equal(AttachedPanelState.ExpandedDialogue, result);
    }

    [Fact]
    public void Idle_respects_disabled_auto_collapse()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00Z"));

        var result = AttachedPanelStateMachine.ApplyIdle(
            AttachedPanelState.ExpandedDialogue,
            time,
            lastInteraction: DateTimeOffset.Parse("2026-08-27T00:00:00Z"),
            idleTimeout: TimeSpan.FromSeconds(30),
            autoCollapseEnabled: false,
            pointerOutside: true);

        Assert.Equal(AttachedPanelState.ExpandedDialogue, result);
    }

    [Fact]
    public void Idle_leaves_collapsed_and_compact_unchanged()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00Z"));
        Assert.Equal(AttachedPanelState.Collapsed, AttachedPanelStateMachine.ApplyIdle(
            AttachedPanelState.Collapsed, time, time.GetUtcNow().AddMinutes(-5), TimeSpan.FromSeconds(30), true, true));
        Assert.Equal(AttachedPanelState.Compact, AttachedPanelStateMachine.ApplyIdle(
            AttachedPanelState.Compact, time, time.GetUtcNow().AddMinutes(-5), TimeSpan.FromSeconds(30), true, true));
    }

    [Fact]
    public void Idle_does_not_collapse_while_a_custom_preset_is_being_edited()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-27T09:00:00Z"));

        var result = AttachedPanelStateMachine.ApplyIdle(
            AttachedPanelState.ExpandedFocus,
            time,
            lastInteraction: DateTimeOffset.Parse("2026-08-27T08:00:00Z"),
            idleTimeout: TimeSpan.FromSeconds(30),
            autoCollapseEnabled: true,
            pointerOutside: true,
            isEditingCustomPreset: true);

        Assert.Equal(AttachedPanelState.ExpandedFocus, result);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}