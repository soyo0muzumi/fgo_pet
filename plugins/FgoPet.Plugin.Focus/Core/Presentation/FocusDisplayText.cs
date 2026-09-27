using FgoPet.Core.Focus;

namespace FgoPet.App.Panels;

/// <summary>Read-only Focus presentation text derived from the authoritative session; never persisted as business state.</summary>
public sealed record FocusDisplayText(string Phase, string Cycle, string Remaining)
{
    public static FocusDisplayText From(FocusSession session) => new(
        Phase: FormatPhase(session),
        Cycle: FormatCycle(session),
        Remaining: FormatClock(session.RemainingSeconds));

    private static string FormatPhase(FocusSession session) =>
        session.Phase == FocusPhase.Break
            ? "休息"
            : session.Status is FocusStatus.PausedFocus or FocusStatus.PausedBreak
                ? "已暂停"
                : "专注中";

    private static string FormatCycle(FocusSession session) =>
        session.TotalCycles > 0 ? $"第 {session.CurrentCycle} / {session.TotalCycles} 轮" : string.Empty;

    private static string FormatClock(int totalSeconds) =>
        $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
}
