using FgoPet.Core.Bond;
using Xunit;

namespace FgoPet.Core.Tests.Bond;

public sealed class DefaultBondProgressionPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3_599, 1)]
    [InlineData(3_600, 2)]
    [InlineData(10_800, 3)]
    [InlineData(162_000, 10)]
    public void Evaluate_uses_the_approved_cumulative_curve(long seconds, int expectedLevel)
    {
        var result = new DefaultBondProgressionPolicy().Evaluate(seconds, achievedLevel: 1);
        Assert.Equal(expectedLevel, result.Level);
    }

    [Fact]
    public void Evaluate_never_downgrades_an_achieved_level() =>
        Assert.Equal(7, new DefaultBondProgressionPolicy().Evaluate(0, achievedLevel: 7).Level);

    [Fact]
    public void Evaluate_caps_at_level_ten()
    {
        var result = new DefaultBondProgressionPolicy().Evaluate(long.MaxValue / 2, achievedLevel: 1);
        Assert.Equal(10, result.Level);
        Assert.True(result.IsMaxLevel);
    }

    [Fact]
    public void Evaluate_reports_progress_toward_the_next_level()
    {
        var result = new DefaultBondProgressionPolicy().Evaluate(5_400, achievedLevel: 1);
        Assert.Equal(2, result.Level);
        Assert.Equal(3_600, result.CurrentThresholdSeconds);
        Assert.Equal(10_800, result.NextThresholdSeconds);
        Assert.False(result.IsMaxLevel);
    }

    [Fact]
    public void Evaluate_rejects_negative_seconds() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DefaultBondProgressionPolicy().Evaluate(-1, achievedLevel: 1));
}
