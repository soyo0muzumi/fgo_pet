namespace FgoPet.Core.Panels;

public enum AttachedPanelState
{
    Collapsed,
    Compact,
    ExpandedFocus,
    ExpandedToday,
    ExpandedDialogue,
    ExpandedTodo,
}

public enum PanelAction
{
    PortraitClick,
    FocusClick,
    TodayClick,
    DialogueClick,
    TodoClick,
    Escape,
}
