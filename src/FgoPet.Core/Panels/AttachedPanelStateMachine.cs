namespace FgoPet.Core.Panels;

/// <summary>
/// Deterministic state machine for the attached panel. Startup always begins
/// <see cref="AttachedPanelState.Collapsed"/>. Expanded states step down to
/// <see cref="AttachedPanelState.Compact"/> on Escape/collapse; an unused action on a
/// given state is a no-op.
/// </summary>
public static class AttachedPanelStateMachine
{
    public static AttachedPanelState Transition(AttachedPanelState from, PanelAction action) => (from, action) switch
    {
        (AttachedPanelState.Collapsed, PanelAction.PortraitClick) => AttachedPanelState.Compact,

        (AttachedPanelState.Compact, PanelAction.DialogueClick) => AttachedPanelState.ExpandedDialogue,
        (AttachedPanelState.Compact, PanelAction.TodoClick) => AttachedPanelState.ExpandedTodo,
        (AttachedPanelState.Compact, PanelAction.Escape) => AttachedPanelState.Collapsed,
        (AttachedPanelState.Compact, PanelAction.PortraitClick) => AttachedPanelState.Collapsed,

        (AttachedPanelState.ExpandedDialogue, PanelAction.DialogueClick) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedDialogue, PanelAction.TodoClick) => AttachedPanelState.ExpandedTodo,
        (AttachedPanelState.ExpandedDialogue, PanelAction.Escape) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedDialogue, PanelAction.Collapse) => AttachedPanelState.Compact,

        (AttachedPanelState.ExpandedTodo, PanelAction.DialogueClick) => AttachedPanelState.ExpandedDialogue,
        (AttachedPanelState.ExpandedTodo, PanelAction.TodoClick) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedTodo, PanelAction.Escape) => AttachedPanelState.Compact,
        (AttachedPanelState.ExpandedTodo, PanelAction.Collapse) => AttachedPanelState.Compact,

        (AttachedPanelState.Collapsed, PanelAction.Collapse) => AttachedPanelState.Collapsed,

        _ => from,
    };

    /// <summary>
    /// Idle behavior: an expanded state auto-collapses to <see cref="AttachedPanelState.Compact"/>
    /// after <paramref name="idleTimeout"/> with no interaction and the pointer outside the
    /// portrait/panel. Auto-collapse can be disabled; Collapsed and Compact are unchanged.
    /// </summary>
    public static AttachedPanelState ApplyIdle(
        AttachedPanelState state,
        TimeProvider time,
        DateTimeOffset lastInteraction,
        TimeSpan idleTimeout,
        bool autoCollapseEnabled,
        bool pointerOutside)
    {
        if (!autoCollapseEnabled || !pointerOutside)
        {
            return state;
        }

        var now = time.GetUtcNow();
        if (now - lastInteraction < idleTimeout)
        {
            return state;
        }

        return state is AttachedPanelState.ExpandedDialogue or AttachedPanelState.ExpandedTodo
            ? AttachedPanelState.Compact
            : state;
    }
}