using FgoPet.Core.Geometry;
using Xunit;

namespace FgoPet.App.Tests.Panels;

public sealed class AttachedPanelLayoutIntegrationTests
{
    private static readonly DeviceRect WorkArea = new(0, 0, 2560, 1440);
    private static readonly DeviceRect Portrait = new(200, 0, 606, 1220);

    [Fact]
    public void A_large_font_panel_is_capped_to_sixty_percent_of_the_work_area()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(503, 720),
            desired: new DeviceSize(480, 3000), // large-font content
            workArea: WorkArea,
            portrait: Portrait);

        Assert.Equal(864, placement.Bounds.Height); // floor(1440 * 0.6)
        Assert.True(placement.Bounds.Bottom <= WorkArea.Bottom);
    }

    [Fact]
    public void The_panel_flips_to_the_right_when_the_left_side_cannot_fit()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(60, 720),
            desired: new DeviceSize(480, 300),
            workArea: WorkArea,
            portrait: Portrait);

        Assert.Equal(PanelSide.Right, placement.Side);
        Assert.True(placement.Bounds.X >= WorkArea.X);
        Assert.True(placement.Bounds.Right <= WorkArea.Right);
    }

    [Fact]
    public void The_expanded_panel_never_intercepts_an_empty_work_area()
    {
        var placement = AttachedPanelLayout.Place(
            anchor: new DevicePoint(503, 720),
            desired: new DeviceSize(1, 1),
            workArea: WorkArea,
            portrait: Portrait);

        Assert.Single(new[] { placement.Bounds });
        Assert.Equal(1, placement.Bounds.Height);
    }
}