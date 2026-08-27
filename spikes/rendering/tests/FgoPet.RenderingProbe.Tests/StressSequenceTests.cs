using FgoPet.RenderingProbe.Diagnostics;

namespace FgoPet.RenderingProbe.Tests;

public sealed class StressSequenceTests
{
    [Fact]
    public void Create_repeats_28_row_major_expressions_ten_times()
    {
        var ids = new[] { "full_body" }.Concat(
            from row in Enumerable.Range(1, 7)
            from column in Enumerable.Range(1, 4)
            select $"r{row:00}c{column:00}");

        var sequence = StressSequence.Create(ids);

        Assert.Equal(280, sequence.Count);
        Assert.Equal("r01c01", sequence[0]);
        Assert.Equal("r07c04", sequence[27]);
        Assert.Equal("r01c01", sequence[28]);
        Assert.DoesNotContain("full_body", sequence);
    }
}
