namespace FgoPet.Core.Focus;

public enum FocusPhase
{
    Focus,
    Break,
}

public static class FocusPhaseKeys
{
    public const string Focus = "focus";
    public const string Break = "break";

    public static string Key(FocusPhase phase) => phase switch
    {
        FocusPhase.Focus => Focus,
        FocusPhase.Break => Break,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
