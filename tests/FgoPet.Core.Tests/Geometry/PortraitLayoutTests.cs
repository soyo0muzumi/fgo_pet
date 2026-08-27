using FgoPet.Core.Geometry;
using Xunit;

namespace FgoPet.Core.Tests.Geometry;

public sealed class PortraitLayoutTests
{
    [Theory]
    [InlineData(1.5, 2.0)]
    [InlineData(2.0, 1.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    public void Calculate_aligns_every_edge_with_one_transform(double x, double y)
    {
        var result = PortraitLayout.Calculate(GeometryFixture.MashGeometry, .5, new Dpi2(x, y));

        Assert.Equal(GeometryFixture.Round(13 * .5 * x), result.OverlayDeviceRect.X);
        Assert.Equal(GeometryFixture.Round(360 * .5 * y), result.PanelAnchorDevice.Y);
        Assert.Equal(result.BodyDeviceRect.Bottom, result.BottomAnchorDevice.Y);
    }

    [Fact]
    public void Calculate_mash_at_200_percent_matches_phase_zero_anchors()
    {
        var result = PortraitLayout.Calculate(GeometryFixture.MashGeometry, 0.50, new Dpi2(2.0, 2.0));

        Assert.Equal(new DeviceRect(13, 0, 256, 240), result.OverlayDeviceRect);
        Assert.Equal(new DeviceRect(0, 0, 303, 603), result.BodyDeviceRect);
        Assert.Equal(new DevicePoint(151, 360), result.PanelAnchorDevice);
        Assert.Equal(new DevicePoint(152, 603), result.BottomAnchorDevice);
    }

    [Fact]
    public void Calculate_logical_rects_derive_from_aligned_device_rects()
    {
        var result = PortraitLayout.Calculate(GeometryFixture.MashGeometry, 0.50, new Dpi2(1.5, 2.0));

        Assert.Equal(result.OverlayDeviceRect.X / 1.5, result.OverlayLogicalRect.X, precision: 6);
        Assert.Equal(result.OverlayDeviceRect.Y / 2.0, result.OverlayLogicalRect.Y, precision: 6);
        Assert.Equal(result.BodyDeviceRect.Width / 1.5, result.BodyLogicalRect.Width, precision: 6);
        Assert.Equal(result.BodyDeviceRect.Height / 2.0, result.BodyLogicalRect.Height, precision: 6);
    }

    [Fact]
    public void Calculate_overlay_sits_within_the_body()
    {
        var result = PortraitLayout.Calculate(GeometryFixture.MashGeometry, 0.75, new Dpi2(1.0, 1.0));

        Assert.True(result.OverlayDeviceRect.X >= result.BodyDeviceRect.X);
        Assert.True(result.OverlayDeviceRect.Right <= result.BodyDeviceRect.Right);
        Assert.True(result.OverlayDeviceRect.Bottom <= result.BodyDeviceRect.Bottom);
    }

    [Fact]
    public void Calculate_requires_positive_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PortraitLayout.Calculate(GeometryFixture.MashGeometry, 0, new Dpi2(1.0, 1.0)));
    }
}