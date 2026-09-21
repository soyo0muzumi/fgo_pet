namespace FgoPet.Core.Events;

/// <summary>Stable persisted event type strings (never enum ordinals).</summary>
public static class RuntimeEventType
{
    public const string FocusStarted = "focus_started";
    public const string FocusCompleted = "focus_completed";
    public const string FocusStopped = "focus_stopped";
    public const string CycleCompleted = "cycle_completed";
    public const string BondLevelUp = "bond_level_up";
}
