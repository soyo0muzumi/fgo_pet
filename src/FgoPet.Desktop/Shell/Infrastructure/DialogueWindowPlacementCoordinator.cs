using System.Runtime.CompilerServices;
using System.Windows;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;

namespace FgoPet.App.Windowing;

/// <summary>
/// Places the standalone dialogue window beside the portrait's current position,
/// then constrains its size and bounds to the selected monitor's work area.
/// </summary>
public sealed class DialogueWindowPlacementCoordinator
{
    private readonly IScreenLayoutService _screen;
    private readonly ConditionalWeakTable<Window, WindowPlacementSize> _windowSizes = new();

    public DialogueWindowPlacementCoordinator(IWindowPlacementStore placement, IScreenLayoutService screen)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
    }

    /// <summary>Chooses and applies the window location before it is shown.</summary>
    public void ApplyOnOpen(Window window, DeviceRect portraitDeviceBounds)
    {
        ArgumentNullException.ThrowIfNull(window);

        var monitors = _screen.GetMonitors();
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

        var size = _windowSizes.GetValue(window, static current => new WindowPlacementSize(current));
        size.ObserveCurrentSize(window);
        var workArea = portraitMonitor.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0
            || !IsValidDipSize(size.RequestedWidth) || !IsValidDipSize(size.RequestedHeight))
        {
            return;
        }

        var widthDip = Math.Min(Math.Max(size.RequestedWidth, size.OriginalMinWidth), workArea.Width / portraitDpi.X);
        var heightDip = Math.Min(Math.Max(size.RequestedHeight, size.OriginalMinHeight), workArea.Height / portraitDpi.Y);
        var windowWidth = Math.Max(1, (int)Math.Round(widthDip * portraitDpi.X));
        var windowHeight = Math.Max(1, (int)Math.Round(heightDip * portraitDpi.Y));
        window.MinWidth = Math.Min(size.OriginalMinWidth, widthDip);
        window.MinHeight = Math.Min(size.OriginalMinHeight, heightDip);
        window.Width = widthDip;
        window.Height = heightDip;
        size.RecordAppliedSize(widthDip, heightDip, window.MinWidth, window.MinHeight);

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

    /// <summary>Compatibility entry point; dialogue placement is intentionally not persisted.</summary>
    public void SaveOnClose(Window window) => ArgumentNullException.ThrowIfNull(window);

    private static MonitorInfo? SelectMonitorContaining(DeviceRect bounds, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(candidate =>
            bounds.X >= candidate.WorkArea.X && bounds.X < candidate.WorkArea.Right
            && bounds.Y >= candidate.WorkArea.Y && bounds.Y < candidate.WorkArea.Bottom);

    private static bool IsValidDpi(Dpi2 dpi) =>
        double.IsFinite(dpi.X) && dpi.X > 0
        && double.IsFinite(dpi.Y) && dpi.Y > 0;

    private static bool IsValidDipSize(double size) => double.IsFinite(size) && size > 0;

    private sealed class WindowPlacementSize(Window window)
    {
        private double _lastAppliedWidth = double.NaN;
        private double _lastAppliedHeight = double.NaN;
        private double _lastAppliedMinWidth = double.NaN;
        private double _lastAppliedMinHeight = double.NaN;
        private bool _hasAppliedSize;

        public double RequestedWidth { get; private set; } = window.Width;
        public double RequestedHeight { get; private set; } = window.Height;
        public double OriginalMinWidth { get; private set; } = window.MinWidth;
        public double OriginalMinHeight { get; private set; } = window.MinHeight;

        public void ObserveCurrentSize(Window current)
        {
            if (!_hasAppliedSize)
            {
                return;
            }

            if (IsValidDipSize(current.Width) && !current.Width.Equals(_lastAppliedWidth))
            {
                RequestedWidth = current.Width;
            }

            if (IsValidDipSize(current.Height) && !current.Height.Equals(_lastAppliedHeight))
            {
                RequestedHeight = current.Height;
            }

            if (!current.MinWidth.Equals(_lastAppliedMinWidth))
            {
                OriginalMinWidth = current.MinWidth;
            }

            if (!current.MinHeight.Equals(_lastAppliedMinHeight))
            {
                OriginalMinHeight = current.MinHeight;
            }
        }

        public void RecordAppliedSize(double width, double height, double minWidth, double minHeight)
        {
            _lastAppliedWidth = width;
            _lastAppliedHeight = height;
            _lastAppliedMinWidth = minWidth;
            _lastAppliedMinHeight = minHeight;
            _hasAppliedSize = true;
        }
    }
}
