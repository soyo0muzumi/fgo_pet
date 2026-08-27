using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using Xunit;

namespace FgoPet.Core.Tests.Windowing;

public sealed class ScreenLayoutTests
{
    private static readonly IReadOnlyList<MonitorInfo> TwoMonitors = new[]
    {
        new MonitorInfo("primary", new DeviceRect(0, 0, 2000, 1000), IsPrimary: true),
        new MonitorInfo("right", new DeviceRect(2000, 0, 2000, 1000), IsPrimary: false),
    };

    [Fact]
    public void Restore_uses_the_saved_monitor_id()
    {
        var saved = new SavedPlacement("right", new DeviceRect(2500, 100, 300, 200));

        var result = ScreenLayout.Restore(saved, TwoMonitors, new DeviceSize(300, 200));

        Assert.Equal(new DeviceRect(2500, 100, 300, 200), result);
    }

    [Fact]
    public void Restore_falls_back_to_the_monitor_with_maximum_overlap()
    {
        // Fully inside the right monitor, so overlap with "right" is maximal.
        var saved = new SavedPlacement(MonitorId: null, new DeviceRect(2200, 960, 400, 200));

        var result = ScreenLayout.Restore(saved, TwoMonitors, new DeviceSize(400, 200));

        // A primary-only clamp would pull X back to 1936; staying 2200 proves "right" was selected.
        Assert.Equal(2200, result.X);
    }

    [Fact]
    public void Restore_falls_back_to_the_primary_monitor_when_no_overlap_exists()
    {
        var saved = new SavedPlacement("missing", new DeviceRect(5000, 500, 200, 100));

        var result = ScreenLayout.Restore(saved, TwoMonitors, new DeviceSize(200, 100));

        // Clamped into the primary work area (0..2000, 0..1000) keeping the drag strip visible.
        Assert.Equal(1936, result.X); // 2000 - 64
        Assert.Equal(500, result.Y);
        Assert.Equal(new DeviceSize(200, 100), new DeviceSize(result.Width, result.Height));
    }

    [Fact]
    public void Restore_clamps_negative_coordinates_keeping_the_drag_region_visible()
    {
        var saved = new SavedPlacement("primary", new DeviceRect(-400, -300, 300, 200));

        var result = ScreenLayout.Restore(saved, TwoMonitors, new DeviceSize(300, 200));

        Assert.Equal(-236, result.X); // keeps 64 device px visible
        Assert.Equal(-136, result.Y);
        Assert.Equal(64, result.Right);   // 64 px of the portrait is on-screen
        Assert.Equal(64, result.Bottom);
    }

    [Fact]
    public void Restore_clamps_off_the_bottom_and_right_edges()
    {
        var saved = new SavedPlacement("primary", new DeviceRect(2100, 1100, 300, 200));

        var result = ScreenLayout.Restore(saved, TwoMonitors, new DeviceSize(300, 200));

        Assert.Equal(1936, result.X); // work area right minus 64
        Assert.Equal(936, result.Y);  // work area bottom minus 64
    }

    [Fact]
    public void Restore_preserves_the_requested_window_size()
    {
        var saved = new SavedPlacement("primary", new DeviceRect(100, 100, 0, 0));

        var result = ScreenLayout.Restore(saved, TwoMonitors, new DeviceSize(320, 480));

        Assert.Equal(new DeviceRect(100, 100, 320, 480), result);
    }

    [Fact]
    public void Restore_requires_at_least_one_monitor()
    {
        Assert.Throws<ArgumentException>(() =>
            ScreenLayout.Restore(new SavedPlacement(null, new DeviceRect(0, 0, 1, 1)), Array.Empty<MonitorInfo>(), new DeviceSize(100, 100)));
    }
}