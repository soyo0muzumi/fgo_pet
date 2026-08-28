using FgoPet.Core.Focus;
using Xunit;

namespace FgoPet.Core.Tests.Focus;

public sealed class FocusPresetTests
{
    [Theory]
    [InlineData(4, 5, 4)]
    [InlineData(181, 5, 4)]
    [InlineData(25, 0, 4)]
    [InlineData(25, 61, 4)]
    [InlineData(25, 5, 0)]
    [InlineData(25, 5, 13)]
    public void Create_rejects_values_outside_the_approved_bounds(int focus, int rest, int cycles) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FocusPreset.Create(focus, rest, cycles));

    [Fact]
    public void Total_seconds_excludes_the_break_after_the_last_cycle()
    {
        var preset = FocusPreset.Create(35, 10, 3);
        Assert.Equal(7_500, preset.TotalSeconds);
    }

    [Fact]
    public void Built_in_presets_match_the_approved_constants()
    {
        var shortPreset = FocusPreset.Create(25, 5, 4);
        Assert.Equal(1_500, shortPreset.FocusSeconds);
        Assert.Equal(300, shortPreset.BreakSeconds);
        Assert.Equal(4, shortPreset.Cycles);

        var longPreset = FocusPreset.Create(50, 10, 2);
        Assert.Equal(3_000, longPreset.FocusSeconds);
        Assert.Equal(600, longPreset.BreakSeconds);
        Assert.Equal(2, longPreset.Cycles);
    }
}
