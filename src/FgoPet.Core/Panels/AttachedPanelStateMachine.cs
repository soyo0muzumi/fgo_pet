namespace FgoPet.Core.Panels;

/// <summary>
/// Deterministic state machine for the attached panel. Startup always begins
/// <see cref="AttachedPanelState.Collapsed"/>. Any expanded state steps down to
/// <see cref="AttachedPanelState.Compact"/> on Escape and closes on PortraitClick;
/// header switches stretch the same panel between the four columns. An unused
/// action on a given state is a no-op.
/// </summary>
public static class AttachedPanelStateMachine
{
    public static AttachedPanelState Transition(AttachedPanelState from, PanelAction action) => (from, action) switch
    {
        (AttachedPanelState.Collapsed, PanelAction.PortraitClick) => AttachedPanelState.Compact,

        (AttachedPanelState.Compact, PanelAction.FocusClick) => AttachedPanelState.ExpandedFocus,
        (AttachedPanelState.Compact, PanelAction.TodayClick) => AttachedPanelState.ExpandedToday,
        (AttachedPanelState.Compact, PanelAction.DialogueClick) => AttachedPanelState.ExpandedDialogue,
        (AttachedPanelState.Compact, PanelAction.TodoClick) => AttachedPanelState.ExpandedTodo,
        (AttachedPanelState.Compact, PanelAction.Escape) => AttachedPanelState.Collapsed,
        (AttachedPanelState.Compact, PanelAction.PortraitClick) => AttachedPanelState.Collapsed,

        (AttachedPanelState.ExpandedFocus, PanelAction.FocusClick) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedToday, PanelAction.TodayClick) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedDialogue, PanelAction.DialogueClick) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedTodo, PanelAction.TodoClick) => AttachedPanelState.Compact,

        (AttachedPanelState.ExpandedFocus, PanelAction.TodayClick) => AttachedPanelState.ExpandedToday,
        (AttachedPanelState.ExpandedToday, PanelAction.FocusClick) => AttachedPanelState.ExpandedFocus,
        (AttachedPanelState.ExpandedDialogue, PanelAction.TodayClick) => AttachedPanelState.ExpandedToday,
        (AttachedPanelState.ExpandedTodo, PanelAction.TodayClick) => AttachedPanelState.ExpandedToday,
        (AttachedPanelState.ExpandedFocus or AttachedPanelState.ExpandedToday
            or AttachedPanelState.ExpandedDialogue or AttachedPanelState.ExpandedTodo,
            PanelAction.DialogueClick) => AttachedPanelState.ExpandedDialogue,
        (AttachedPanelState.ExpandedFocus or AttachedPanelState.ExpandedToday
            or AttachedPanelState.ExpandedDialogue or AttachedPanelState.ExpandedTodo,
            PanelAction.TodoClick) => AttachedPanelState.ExpandedTodo,

        (AttachedPanelState.ExpandedFocus or AttachedPanelState.ExpandedToday
            or AttachedPanelState.ExpandedDialogue or AttachedPanelState.ExpandedTodo,
            PanelAction.Escape) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedFocus or AttachedPanelState.ExpandedToday
            or AttachedPanelState.ExpandedDialogue or AttachedPanelState.ExpandedTodo,
            PanelAction.PortraitClick) => AttachedPanelState.Collapsed,

        _ => from,
    };

    /// <summary>
    /// Idle behavior: an expanded state auto-collapses to <see cref="AttachedPanelState.Compact"/>
    /// after <paramref name="idleTimeout"/> with no interaction and the pointer outside the
    /// portrait/panel — unless a custom preset is being edited, in which case the panel
    /// stays. Auto-collapse can be disabled; Collapsed and Compact are unchanged.
    /// </summary>
    public static AttachedPanelState ApplyIdle(
        AttachedPanelState state,
        TimeProvider time,
        DateTimeOffset lastInteraction,
        TimeSpan idleTimeout,
        bool autoCollapseEnabled,
        bool pointerOutside,
        bool isEditingCustomPreset = false)
    {
        if (!autoCollapseEnabled || !pointerOutside || isEditingCustomPreset)
        {
            return state;
        }

        var now = time.GetUtcNow();
        if (now - lastInteraction < idleTimeout)
        {
            return state;
        }

        return IsExpanded(state) ? AttachedPanelState.Compact : state;
    }

    /// <summary>True for all four stretch states; single check used by idle logic.</summary>
    public static bool IsExpanded(AttachedPanelState state) => state switch
    {
        AttachedPanelState.ExpandedFocus
            or AttachedPanelState.ExpandedToday
            or AttachedPanelState.ExpandedDialogue
            or AttachedPanelState.ExpandedTodo => true,
        _ => false,
    };
}
