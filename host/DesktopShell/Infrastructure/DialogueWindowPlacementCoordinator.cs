using System.Windows;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;

namespace FgoPet.App.Windowing;

/// <summary>
/// Places the standalone dialogue window (spec §8.1 R5): restore the saved
/// "dialogue" slot when its monitor still exists; otherwise default beside the
/// portrait on the portrait's monitor without overlapping the pet column, then
/// clamp fully visible. Saving happens whenever the window hides.
/// </summary>
public sealed class DialogueWindowPlacementCoordinator
{
    private readonly IWindowPlacementStore _placement;
    private readonly IScreenLayoutService _screen;

    public DialogueWindowPlacementCoordinator(IWindowPlacementStore placement, IScreenLayoutService screen)
    {
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
    }

    /// <summary>Chooses and applies the window location before it is shown.</summary>
    public void ApplyOnOpen(Window window, DeviceRect portraitDeviceBounds)
    {
        var monitors = _screen.GetMonitors();
        var saved = _placement.Load(WindowPlacementSlots.Dialogue);
        var savedMonitor = saved?.MonitorId is { } id
            ? monitors.FirstOrDefault(candidate => candidate.Id == id)
            : null;
        if (saved is not null && savedMonitor is not null)
        {
            var dpi = _screen.GetDpi(savedMonitor.Id);
            if (IsValidDpi(dpi))
            {
                var width = Math.Max(1, (int)Math.Round(saved.WindowWidthDip * dpi.X));
                var height = Math.Max(1, (int)Math.Round(saved.WindowHeightDip * dpi.Y));
                var x = savedMonitor.WorkArea.X + (int)Math.Round(saved.OffsetX * dpi.X);
                var y = savedMonitor.WorkArea.Y + (int)Math.Round(saved.OffsetY * dpi.Y);
                var clamped = ScreenLayout.ClampFullyVisible(new DeviceRect(x, y, width, height), savedMonitor.WorkArea);
                window.Left = clamped.X / dpi.X;
                window.Top = clamped.Y / dpi.Y;
                window.Width = saved.WindowWidthDip;
                window.Height = saved.WindowHeightDip;
                return;
            }
        }

        var portraitMonitor = SelectMonitorContaining(portraitDeviceBounds, monitors)
            ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors.FirstOrDefault();
        if (portraitMonitor is null)
        {
            return;
        }

        var portraitDpi = _screen.GetDpi(portraitMonitor.Id);
        if (!IsValidDpi(portraitDpi))
        {
            return;
        }

        var workArea = portraitMonitor.WorkArea;
        var windowWidth = Math.Min((int)Math.Round(window.Width * portraitDpi.X), workArea.Width);
        var windowHeight = Math.Min((int)Math.Round(window.Height * portraitDpi.Y), workArea.Height);
        var portraitRight = portraitDeviceBounds.X + portraitDeviceBounds.Width;
        var gap = (int)Math.Round(12 * portraitDpi.X);
        var rightCandidate = portraitRight + gap;
        DeviceRect placed;
        if (rightCandidate + windowWidth <= workArea.Right)
        {
            placed = new DeviceRect(rightCandidate, portraitDeviceBounds.Y, windowWidth, windowHeight);
        }
        else
        {
            var leftCandidate = portraitDeviceBounds.X - gap - windowWidth;
            var x = leftCandidate >= workArea.X
                ? leftCandidate
                : workArea.X + Math.Max(0, (workArea.Width - windowWidth) / 2);
            placed = new DeviceRect(x, portraitDeviceBounds.Y, windowWidth, windowHeight);
        }

        var visible = ScreenLayout.ClampFullyVisible(placed, workArea);
        window.Left = visible.X / portraitDpi.X;
        window.Top = visible.Y / portraitDpi.Y;
    }

    /// <summary>Persists the current window bounds into the "dialogue" slot as work-area-relative DIP.</summary>
    public void SaveOnClose(Window window)
    {
        var leftDip = double.IsFinite(window.Left) ? window.Left : 0;
        var topDip = double.IsFinite(window.Top) ? window.Top : 0;
        var widthDip = double.IsFinite(window.ActualWidth) && window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var heightDip = double.IsFinite(window.ActualHeight) && window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        var monitor = _screen.GetMonitors().FirstOrDefault(candidate =>
                leftDip >= candidate.WorkArea.X && leftDip < candidate.WorkArea.Right
                && topDip >= candidate.WorkArea.Y && topDip < candidate.WorkArea.Bottom)
            ?? _screen.GetMonitors().FirstOrDefault(candidate => candidate.IsPrimary)
            ?? _screen.GetMonitors().FirstOrDefault();
        if (monitor is null)
        {
            return;
        }

        var dpi = _screen.GetDpi(monitor.Id);
        if (!IsValidDpi(dpi))
        {
            dpi = new Dpi2(1.0, 1.0);
        }

        _placement.Save(WindowPlacementSlots.Dialogue, new WindowPlacement(
            monitor.Id,
            leftDip - (monitor.WorkArea.X / dpi.X),
            topDip - (monitor.WorkArea.Y / dpi.Y),
            dpi.X,
            dpi.Y,
            widthDip,
            heightDip));
    }

    private static MonitorInfo? SelectMonitorContaining(DeviceRect bounds, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(candidate =>
            bounds.X >= candidate.WorkArea.X && bounds.X < candidate.WorkArea.Right
            && bounds.Y >= candidate.WorkArea.Y && bounds.Y < candidate.WorkArea.Bottom);

    private static bool IsValidDpi(Dpi2 dpi) =>
        double.IsFinite(dpi.X) && dpi.X > 0
        && double.IsFinite(dpi.Y) && dpi.Y > 0;
}
