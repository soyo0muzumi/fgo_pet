using FgoPet.Core.Geometry;

namespace FgoPet.Core.Windowing;

public sealed record MonitorInfo(string Id, DeviceRect WorkArea, bool IsPrimary);

/// <summary>
/// Where the portrait window was when it was last visible: the display it was on plus
/// its absolute device-pixel rectangle on the virtual desktop.
/// </summary>
public sealed record SavedPlacement(
    string? MonitorId,
    DeviceRect AbsoluteWindowRect);

/// <summary>
/// Restores a portrait window. Selection order is: the saved monitor ID, the monitor
/// with the maximum overlap against the saved rectangle, then the primary monitor.
/// The remembered absolute position is clamped to the selected monitor's work area so
/// the portrait's drag region stays visible.
/// </summary>
public static class ScreenLayout
{
    private const int DragPreserveWidth = 64;
    private const int DragPreserveHeight = 64;

    public static DeviceRect Restore(SavedPlacement saved, IReadOnlyList<MonitorInfo> monitors, DeviceSize window)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }
        if (window.Width <= 0 || window.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        var matched = saved.MonitorId is not null
            ? monitors.FirstOrDefault(monitor => monitor.Id == saved.MonitorId)
            : null;
        var selected = matched ?? monitors
            .OrderByDescending(monitor => IntersectArea(saved.AbsoluteWindowRect, monitor.WorkArea))
            .ThenBy(monitor => monitor.IsPrimary ? 0 : 1)
            .First();

        return Clamp(
            new DevicePoint(saved.AbsoluteWindowRect.X, saved.AbsoluteWindowRect.Y),
            selected.WorkArea,
            window);
    }

    public static DeviceRect ClampFullyVisible(DeviceRect portrait, DeviceRect workArea)
    {
        if (portrait.Width <= 0 || portrait.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(portrait));
        }
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }

        var width = Math.Min(portrait.Width, workArea.Width);
        var height = Math.Min(portrait.Height, workArea.Height);
        return new DeviceRect(
            Math.Clamp(portrait.X, workArea.Left, workArea.Right - width),
            Math.Clamp(portrait.Y, workArea.Top, workArea.Bottom - height),
            width,
            height);
    }

    private static DeviceRect Clamp(DevicePoint desired, DeviceRect workArea, DeviceSize window)
    {
        var left = Math.Clamp(
            desired.X,
            workArea.X - window.Width + DragPreserveWidth,
            workArea.Right - DragPreserveWidth);
        var top = Math.Clamp(
            desired.Y,
            workArea.Y - window.Height + DragPreserveHeight,
            workArea.Bottom - DragPreserveHeight);
        return new DeviceRect(left, top, window.Width, window.Height);
    }

    private static int IntersectArea(DeviceRect a, DeviceRect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        return Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
    }
}
