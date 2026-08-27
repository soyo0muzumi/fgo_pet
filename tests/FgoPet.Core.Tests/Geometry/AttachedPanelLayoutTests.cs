using FgoPet.Core.Geometry;
using Xunit;

namespace FgoPet.Core.Tests.Geometry;

public sealed class AttachedPanelLayoutTests
{
    private static readonly DeviceRect WorkArea = new(0, 0, 2000, 1000);
    private static readonly DeviceRect FullWidthPortrait = new(0, 0, 2000, 600);

    [Fact]
    public void Place_prefers_left_when_both_sides_fit_equally()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(1200, 500),
            desired: new DeviceSize(300, 200),
            workArea: WorkArea,
            portrait: FullWidthPortrait);

        Assert.Equal(PanelSide.Left, placement.Side);
        Assert.False(placement.FlippedHorizontally);
        Assert.Equal(900, placement.Bounds.X); // right edge flush with the anchor
        Assert.Equal(1200, placement.Bounds.Right);
        Assert.Equal(500, placement.Bounds.Y);
    }

    [Fact]
    public void Place_prefers_right_when_left_occludes_the_portrait_more()
    {
        var portrait = new DeviceRect(500, 0, 400, 600);
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(1000, 300),
            desired: new DeviceSize(300, 200),
            workArea: WorkArea,
            portrait: portrait);

        Assert.Equal(PanelSide.Right, placement.Side);
        Assert.True(placement.FlippedHorizontally);
        Assert.Equal(1000, placement.Bounds.X);
        Assert.Equal(1300, placement.Bounds.Right);
    }

    [Fact]
    public void Place_flips_right_when_left_leaves_the_work_area()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(50, 500),
            desired: new DeviceSize(300, 200),
            workArea: WorkArea,
            portrait: FullWidthPortrait);

        Assert.Equal(PanelSide.Right, placement.Side);
        Assert.True(placement.FlippedHorizontally);
        Assert.Equal(new DeviceRect(50, 500, 300, 200), placement.Bounds);
    }

    [Fact]
    public void Place_clamps_vertically_at_the_work_area_bottom()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(1200, 950),
            desired: new DeviceSize(300, 200),
            workArea: WorkArea,
            portrait: FullWidthPortrait);

        Assert.True(placement.ClampedVertically);
        Assert.Equal(800, placement.Bounds.Y);
        Assert.Equal(1000, placement.Bounds.Bottom);
    }

    [Fact]
    public void Place_caps_expanded_height_to_sixty_percent_of_the_work_area()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(1200, 300),
            desired: new DeviceSize(300, 2000),
            workArea: WorkArea,
            portrait: FullWidthPortrait);

        Assert.Equal(600, placement.Bounds.Height); // floor(1000 * 0.6)
        Assert.Equal(900, placement.Bounds.Bottom);
    }

    [Fact]
    public void Place_caps_width_to_the_work_area()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(1200, 300),
            desired: new DeviceSize(5000, 200),
            workArea: WorkArea,
            portrait: FullWidthPortrait);

        Assert.Equal(2000, placement.Bounds.Width);
    }

    [Fact]
    public void Place_into_a_corner_flips_right_and_clamps_vertically()
    {
        var cornerWorkArea = new DeviceRect(0, 0, 1200, 800);
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(0, 750),
            desired: new DeviceSize(400, 700),
            workArea: cornerWorkArea,
            portrait: new DeviceRect(0, 0, 300, 600));

        Assert.Equal(PanelSide.Right, placement.Side);
        Assert.True(placement.ClampedVertically);
        Assert.Equal(new DeviceRect(0, 320, 400, 480), placement.Bounds);
        Assert.Equal(800, placement.Bounds.Bottom);
    }

    [Fact]
    public void Place_requires_positive_desired_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AttachedPanelLayout.Place(new DevicePoint(0, 0), new DeviceSize(0, 100), WorkArea, FullWidthPortrait));
    }
}