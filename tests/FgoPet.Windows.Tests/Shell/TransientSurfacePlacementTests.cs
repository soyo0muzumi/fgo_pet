using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class TransientSurfacePlacementTests
{
    [Fact]
    public void Peek_flips_to_the_left_and_clamps_to_the_monitor_work_area()
    {
        var workArea = new DeviceRect(1000, 0, 1200, 800);
        var anchor = new DeviceRect(1900, 650, 160, 160);

        var placed = TransientSurfacePlacement.Place(anchor, workArea, new Dpi2(1.5, 1.5), 320, 400);

        Assert.Equal(1900 - 18 - 480, placed.X);
        Assert.Equal(200, placed.Y);
        Assert.Equal(480, placed.Width);
        Assert.Equal(600, placed.Height);
    }
}
