namespace FgoPet.Core.Focus;

/// <summary>
/// Bounded focus/break preset. Approved bounds: focus 5-180 minutes, break 1-60
/// minutes, 1-12 cycles. <see cref="TotalSeconds"/> excludes the break after the
/// last cycle.
/// </summary>
public sealed record FocusPreset(int FocusSeconds, int BreakSeconds, int Cycles)
{
    public int TotalSeconds => checked(FocusSeconds * Cycles + BreakSeconds * (Cycles - 1));

    public static FocusPreset Create(int focusMinutes, int breakMinutes, int cycles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(focusMinutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(focusMinutes, 180);
        ArgumentOutOfRangeException.ThrowIfLessThan(breakMinutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(breakMinutes, 60);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cycles, 12);
        return new(checked(focusMinutes * 60), checked(breakMinutes * 60), cycles);
    }
}
