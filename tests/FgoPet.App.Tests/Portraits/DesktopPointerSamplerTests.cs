using System.Windows;
using FgoPet.App.Portraits.Live2D;
using Xunit;

namespace FgoPet.App.Tests.Portraits;

public sealed class DesktopPointerSamplerTests
{
    [Theory]
    [InlineData(-1920, 100, 200, 300)]
    [InlineData(-3840, 200, 400, 600)]
    public void Physical_screen_bounds_preserve_relative_coordinates_across_monitors_and_dpi(
        double left, double top, double width, double height)
    {
        var sampler = new DesktopPointerSampler();
        Assert.True(sampler.TrySample(new Point(left + width * 1.5, top + height * 0.25),
            new Point(left, top), new Point(left + width, top + height), out var sample));
        Assert.Equal(1.5, sample.X);
        Assert.Equal(0.25, sample.Y);
        Assert.True(sample.Active);
    }

    [Fact]
    public void Window_movement_reprojects_stationary_cursor_without_counting_as_activity()
    {
        var sampler = new DesktopPointerSampler();
        var cursor = new Point(300, 200);
        sampler.TrySample(cursor, new Point(100, 100), new Point(300, 400), out _);
        Assert.True(sampler.TrySample(cursor, new Point(200, 100), new Point(400, 400), out var sample));
        Assert.Equal(0.5, sample.X);
        Assert.False(sample.Active);
        sampler.TrySample(new Point(302, 200), new Point(200, 100), new Point(400, 400), out sample);
        Assert.False(sample.Active);
        sampler.TrySample(new Point(304, 200), new Point(200, 100), new Point(400, 400), out sample);
        Assert.True(sample.Active);
    }

    [Fact]
    public void Invalid_or_zero_size_bounds_do_not_emit_packets()
    {
        var sampler = new DesktopPointerSampler();
        Assert.False(sampler.TrySample(new Point(1, 1), new Point(), new Point(), out _));
        Assert.False(sampler.TrySample(new Point(double.NaN, 1), new Point(), new Point(200, 300), out _));
    }
}
