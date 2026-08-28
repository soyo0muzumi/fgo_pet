using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FgoPet.App.Main;
using FgoPet.App.Panels;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
using FgoPet.Core.Panels;
using FgoPet.Core.Portraits;
using FgoPet.Core.Windowing;
using Point = System.Windows.Point;

namespace FgoPet.App.Windowing;

/// <summary>
/// Wires a portrait window to the controller: presents validated states, restores and
/// saves placement, makes transparent pixels pass through via <c>WM_NCHITTEST</c>, and
/// turns press/move/release into click/drag using the system drag threshold.
/// </summary>
public sealed class PortraitWindowCoordinator : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const int WmDpiChanged = 0x02E0;
    private const nint HtTransparent = -1;
    private const nint HtClient = 1;

    private readonly PortraitWindow _window;
    private readonly PortraitController _controller;
    private readonly IWindowPlacementStore _placement;
    private readonly IScreenLayoutService _screen;
    private readonly PointerGestureRecognizer _gesture = new();
    private HwndSource? _source;
    private Dpi2 _dpi = new(1.0, 1.0);
    private bool _dragging;
    private bool _pressWasOnPortrait;

    public PortraitWindowCoordinator(
        PortraitWindow window,
        PortraitController controller,
        IWindowPlacementStore placement,
        IScreenLayoutService screen)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));

        _controller.StateChanged += OnControllerStateChanged;
        _window.AttachedPanel.PropertyChanged += OnPanelPropertyChanged;
        _window.SourceInitialized += (_, _) => AttachHook();
        _window.Closing += (_, _) => SavePlacement();
        _window.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsButton(e.OriginalSource as DependencyObject))
            {
                return;
            }
            if (OnPointerDown(e.GetPosition(_window)))
            {
                e.Handled = true;
            }
        };
        _window.PreviewMouseMove += (_, e) =>
        {
            if (OnPointerMove(e.GetPosition(_window)))
            {
                e.Handled = true;
            }
        };
        _window.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (OnPointerUp(e.GetPosition(_window)))
            {
                e.Handled = true;
            }
        };
    }

    public void RestorePlacement()
    {
        var saved = _placement.Load();
        if (saved is null)
        {
            return;
        }

        var monitors = _screen.GetMonitors();
        var savedMonitor = monitors.FirstOrDefault(monitor => monitor.Id == saved.MonitorId);
        if (savedMonitor is not null)
        {
            var deviceSize = new DeviceSize(
                (int)Math.Round(saved.WindowWidthDip * saved.SavedDpiX),
                (int)Math.Round(saved.WindowHeightDip * saved.SavedDpiY));
            var savedDevice = new SavedPlacement(
                saved.MonitorId,
                new DeviceRect(
                    savedMonitor.WorkArea.X + (int)Math.Round(saved.OffsetX * saved.SavedDpiX),
                    savedMonitor.WorkArea.Y + (int)Math.Round(saved.OffsetY * saved.SavedDpiY),
                    deviceSize.Width,
                    deviceSize.Height));
            var restored = ScreenLayout.Restore(savedDevice, monitors, deviceSize);
            _window.Left = restored.X / _dpi.X;
            _window.Top = restored.Y / _dpi.Y;
            _window.Width = restored.Width / _dpi.X;
            _window.Height = restored.Height / _dpi.Y;
        }
    }

    public void Dispose()
    {
        _controller.StateChanged -= OnControllerStateChanged;
        _window.AttachedPanel.PropertyChanged -= OnPanelPropertyChanged;
        _source?.RemoveHook(OnWindowMessage);
    }

    private void AttachHook()
    {
        var dpi = VisualTreeHelper.GetDpi(_window);
        ApplyWindowDpi(new Dpi2(dpi.DpiScaleX, dpi.DpiScaleY));
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                var state = _controller.CurrentState;
                if (state is null)
                {
                    break;
                }

                var lParamValue = lParam.ToInt64();
                var screenPoint = new Point((short)(lParamValue & 0xFFFF), (short)((lParamValue >> 16) & 0xFFFF));
                var logical = _window.PointFromScreen(screenPoint);
                handled = true;
                if (_window.IsAttachedPanelHit(logical))
                {
                    return HtClient;
                }
                return AlphaHitTestService.IsHit(_window.ToPortraitLocal(logical), state.Snapshot, state.ExpressionAssetId, state.Geometry)
                    ? HtClient
                    : HtTransparent;

            case WmDpiChanged:
                var value = wParam.ToInt64();
                ApplyWindowDpi(new Dpi2((short)(value & 0xFFFF) / 96.0, (short)((value >> 16) & 0xFFFF) / 96.0));
                break;
        }

        return IntPtr.Zero;
    }

    internal void ApplyWindowDpi(Dpi2 dpi)
    {
        _dpi = dpi;
        _controller.ApplyDpi(_dpi);
        if (_controller.CurrentState is not null)
        {
            ArrangeAttachedPanel();
        }
    }

    private bool OnPointerDown(Point windowPoint)
    {
        var portraitHit = IsPortraitHit(windowPoint);
        var panelHit = _window.IsAttachedPanelHit(windowPoint);
        if (_dragging || (!portraitHit && !panelHit))
        {
            return false;
        }

        _pressWasOnPortrait = portraitHit;
        _gesture.Press(windowPoint, isSecondary: false);
        return true;
    }

    private bool OnPointerMove(Point windowPoint)
    {
        if (_gesture.Move(windowPoint) == GestureEvent.DragStart && !_dragging)
        {
            _dragging = true;
            _window.DragMove();
            _dragging = false;
            ClampPortraitToWorkArea();
            SavePlacement();
            return true;
        }

        return false;
    }

    private bool OnPointerUp(Point windowPoint)
    {
        var gesture = _gesture.Release(windowPoint);
        if (gesture == GestureEvent.Click && _pressWasOnPortrait)
        {
            _window.HandlePortraitClick();
        }
        _pressWasOnPortrait = false;
        if (gesture == GestureEvent.DragEnd)
        {
            ClampPortraitToWorkArea();
            SavePlacement();
        }
        return gesture != GestureEvent.None;
    }

    private bool IsPortraitHit(Point windowPoint) =>
        _controller.CurrentState is { } state
        && AlphaHitTestService.IsHit(
            _window.ToPortraitLocal(windowPoint),
            state.Snapshot,
            state.ExpressionAssetId,
            state.Geometry);

    internal void ClampPortraitToWorkArea()
    {
        var portrait = _window.PortraitScreenBounds;
        var device = new DeviceRect(
            (int)Math.Round(portrait.X * _dpi.X),
            (int)Math.Round(portrait.Y * _dpi.Y),
            Math.Max(1, (int)Math.Round(portrait.Width * _dpi.X)),
            Math.Max(1, (int)Math.Round(portrait.Height * _dpi.Y)));
        var monitor = SelectNearestMonitor(device, _screen.GetMonitors());
        if (monitor is null)
        {
            return;
        }

        var clamped = ScreenLayout.ClampFullyVisible(device, monitor.WorkArea);
        _window.MovePortraitToDevice(new DevicePoint(clamped.X, clamped.Y), _dpi);
        ArrangeAttachedPanel();
    }

    private static MonitorInfo? SelectNearestMonitor(DeviceRect portrait, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.OrderBy(monitor => DistanceSquared(portrait, monitor.WorkArea)).FirstOrDefault();

    private static long DistanceSquared(DeviceRect portrait, DeviceRect workArea)
    {
        var x = Math.Clamp(portrait.X + (portrait.Width / 2), workArea.Left, workArea.Right);
        var y = Math.Clamp(portrait.Y + (portrait.Height / 2), workArea.Top, workArea.Bottom);
        var dx = (long)portrait.X + (portrait.Width / 2) - x;
        var dy = (long)portrait.Y + (portrait.Height / 2) - y;
        return (dx * dx) + (dy * dy);
    }

    private static bool IsButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }
        return false;
    }

    private void ApplyCurrentState()
    {
        var state = _controller.CurrentState;
        if (state is null)
        {
            _window.Hide();
            return;
        }

        _window.Present(state.Snapshot, state.Geometry);
        _window.PortraitView.SetExpression(state.ExpressionAssetId);
        ArrangeAttachedPanel();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AttachedPanelViewModel.State)
            && _window.AttachedPanel.State != AttachedPanelState.Collapsed)
        {
            ArrangeAttachedPanel();
        }
    }

    private void OnControllerStateChanged(object? sender, EventArgs e) =>
        _window.Dispatcher.BeginInvoke(ApplyCurrentState);

    private void ArrangeAttachedPanel()
    {
        if (_controller.CurrentState is not { } state
            || _window.AttachedPanel.State == AttachedPanelState.Collapsed)
        {
            return;
        }

        var x = (int)Math.Round(_window.Left * _dpi.X);
        var y = (int)Math.Round(_window.Top * _dpi.Y);
        var monitor = _screen.GetMonitors().FirstOrDefault(candidate =>
            x >= candidate.WorkArea.Left && x < candidate.WorkArea.Right
            && y >= candidate.WorkArea.Top && y < candidate.WorkArea.Bottom)
            ?? _screen.GetMonitors().FirstOrDefault(candidate => candidate.IsPrimary)
            ?? _screen.GetMonitors().FirstOrDefault();
        if (monitor is not null)
        {
            _window.ArrangeOverlayPanel(state.Geometry, monitor.WorkArea, _dpi);
        }
    }

    private void SavePlacement()
    {
        var portrait = _window.PortraitScreenBounds;
        var workDeviceX = portrait.X * _dpi.X;
        var workDeviceY = portrait.Y * _dpi.Y;
        var monitor = _screen.GetMonitors().FirstOrDefault(m =>
            workDeviceX >= m.WorkArea.X && workDeviceX < m.WorkArea.Right
            && workDeviceY >= m.WorkArea.Y && workDeviceY < m.WorkArea.Bottom)
            ?? _screen.GetMonitors().FirstOrDefault();

        _placement.Save(new WindowPlacement(
            monitor?.Id,
            (workDeviceX - (monitor?.WorkArea.X ?? 0)) / _dpi.X,
            (workDeviceY - (monitor?.WorkArea.Y ?? 0)) / _dpi.Y,
            _dpi.X,
            _dpi.Y,
            portrait.Width,
            portrait.Height));
    }
}
