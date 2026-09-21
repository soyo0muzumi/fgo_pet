namespace FgoPet.Core.Focus;

/// <summary>Stable persisted status strings for a focus session.</summary>
public enum FocusStatus
{
    Idle,
    Focusing,
    PausedFocus,
    Breaking,
    PausedBreak,
    Completed,
}

public static class FocusStatusKeys
{
    public const string Idle = "idle";
    public const string Focusing = "focusing";
    public const string PausedFocus = "paused_focus";
    public const string Breaking = "breaking";
    public const string PausedBreak = "paused_break";
    public const string Completed = "completed";

    public static string Key(FocusStatus status) => status switch
    {
        FocusStatus.Idle => Idle,
        FocusStatus.Focusing => Focusing,
        FocusStatus.PausedFocus => PausedFocus,
        FocusStatus.Breaking => Breaking,
        FocusStatus.PausedBreak => PausedBreak,
        FocusStatus.Completed => Completed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
