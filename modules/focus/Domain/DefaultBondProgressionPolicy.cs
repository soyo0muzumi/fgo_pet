namespace FgoPet.Core.Bond;

/// <summary>
/// Approved cumulative curve: 1/3/6/10/15/21/28/36/45 effective hours for levels
/// 2-10, capped at <see cref="MaxLevel"/>. Achieved levels never decrease.
/// </summary>
public sealed class DefaultBondProgressionPolicy : IBondProgressionPolicy
{
    private static readonly long[] Thresholds =
        [0, 3_600, 10_800, 21_600, 36_000, 54_000, 75_600, 100_800, 129_600, 162_000];

    public string Version => "bond-v1";

    public int MaxLevel => 10;

    public BondProgress Evaluate(long lifetimeFocusSeconds, int achievedLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lifetimeFocusSeconds);
        var calculated = Array.FindLastIndex(Thresholds, value => lifetimeFocusSeconds >= value) + 1;
        var level = Math.Clamp(Math.Max(calculated, achievedLevel), 1, MaxLevel);
        var current = Thresholds[level - 1];
        var next = level == MaxLevel ? current : Thresholds[level];
        return new BondProgress(level, lifetimeFocusSeconds, current, next, level == MaxLevel);
    }
}
