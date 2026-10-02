namespace FgoPet.Core.Focus;

/// <summary>
/// Immutable session snapshot. <see cref="RemainingSeconds"/> counts down the
/// current phase; <see cref="PhaseElapsedSeconds"/> counts up within it. The
/// servant is captured once at <see cref="Start"/> and never replaced.
/// </summary>
public sealed record FocusSession(
    string SessionId,
    FocusStatus Status,
    int FocusSeconds,
    int BreakSeconds,
    int TotalCycles,
    int CurrentCycle,
    FocusPhase Phase,
    int RemainingSeconds,
    int PhaseElapsedSeconds,
    string ServantId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsCurrent)
{
    public static FocusSession Idle { get; } = new(
        string.Empty, FocusStatus.Idle, 0, 0, 0, 0, FocusPhase.Focus,
        0, 0, string.Empty, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, IsCurrent: false);

    public static FocusSession Start(
        string sessionId, string servantId, FocusPreset preset, DateTimeOffset startedAtUtc) => new(
        sessionId,
        FocusStatus.Focusing,
        preset.FocusSeconds,
        preset.BreakSeconds,
        preset.Cycles,
        CurrentCycle: 1,
        FocusPhase.Focus,
        RemainingSeconds: preset.FocusSeconds,
        PhaseElapsedSeconds: 0,
        servantId,
        startedAtUtc,
        startedAtUtc,
        IsCurrent: true);

    /// <summary>
    /// Maps active states to their paused counterpart for offline recovery without
    /// subtracting wall time; terminal/idle states pass through unchanged.
    /// </summary>
    public FocusSession RestorePaused() => Status switch
    {
        FocusStatus.Focusing => this with { Status = FocusStatus.PausedFocus },
        FocusStatus.Breaking => this with { Status = FocusStatus.PausedBreak },
        _ => this,
    };
}
