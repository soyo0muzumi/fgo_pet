using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
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
        var portraitMonitor = TransientSurfacePlacement.SelectMonitor(portraitDeviceBounds, monitors)
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

        var size = _windowSizes.GetValue(window, current =>
        {
            var state = new WindowPlacementSize(current);
            current.SourceInitialized += (_, _) =>
            {
                TransientSurfacePlacement.ApplyNativeBounds(current, state.NativeBounds);
                HwndSource.FromHwnd(new WindowInteropHelper(current).Handle)?.AddHook(
                    (nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
                    {
                        if (msg is 0x007E or 0x001A && current.IsVisible && !current.Dispatcher.HasShutdownStarted)
                            current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (current.IsVisible && current.WindowState != WindowState.Minimized)
                                    ApplyOnOpen(current,state.Anchor);
                            }));
                        return 0;
                    });
            };
            return state;
        });
        size.Anchor = portraitDeviceBounds;
        size.ObserveCurrentSize(window);
        var workArea = portraitMonitor.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0
            || !IsValidDipSize(size.RequestedWidth) || !IsValidDipSize(size.RequestedHeight))
        {
            return;
        }

        var visible = TransientSurfacePlacement.Place(portraitDeviceBounds, workArea, portraitDpi,
            Math.Max(size.RequestedWidth, size.OriginalMinWidth),
            Math.Max(size.RequestedHeight, size.OriginalMinHeight), marginDip: 16);
        var widthDip = visible.Width / portraitDpi.X;
        var heightDip = visible.Height / portraitDpi.Y;
        window.MinWidth = Math.Min(size.OriginalMinWidth, widthDip);
        window.MinHeight = Math.Min(size.OriginalMinHeight, heightDip);
        window.Width = widthDip;
        window.Height = heightDip;
        size.RecordAppliedSize(widthDip, heightDip, window.MinWidth, window.MinHeight);

        window.Left = visible.X / portraitDpi.X;
        window.Top = visible.Y / portraitDpi.Y;
        size.NativeBounds = visible;
        TransientSurfacePlacement.ApplyNativeBounds(window, visible);
    }

    /// <summary>Compatibility entry point; dialogue placement is intentionally not persisted.</summary>
    public void SaveOnClose(Window window) => ArgumentNullException.ThrowIfNull(window);

    private static bool IsValidDpi(Dpi2 dpi) =>
        double.IsFinite(dpi.X) && dpi.X > 0
        && double.IsFinite(dpi.Y) && dpi.Y > 0;

    private static bool IsValidDipSize(double size) => double.IsFinite(size) && size > 0;

    private sealed class WindowPlacementSize(Window window)
    {
        public DeviceRect NativeBounds { get; set; }
        public DeviceRect Anchor { get; set; }
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
