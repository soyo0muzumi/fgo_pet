using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using FgoPet.App.Main;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
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

        _controller.StateChanged += (_, _) => _window.Dispatcher.BeginInvoke(ApplyCurrentState);
        _window.SourceInitialized += (_, _) => AttachHook();
        _window.Closing += (_, _) => SavePlacement();
        _window.PreviewMouseLeftButtonDown += (_, e) =>
        {
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

    public void Dispose() => _source?.RemoveHook(OnWindowMessage);

    private void AttachHook()
    {
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
                var client = _window.PointFromScreen(screenPoint);
                var logical = new Point(client.X / _dpi.X, client.Y / _dpi.Y);
                handled = true;
                return AlphaHitTestService.IsHit(logical, state.Snapshot, state.ExpressionAssetId, state.Geometry)
                    ? HtClient
                    : HtTransparent;

            case WmDpiChanged:
                var value = wParam.ToInt64();
                _dpi = new Dpi2((short)(value & 0xFFFF) / 96.0, (short)((value >> 16) & 0xFFFF) / 96.0);
                _controller.ApplyDpi(_dpi);
                break;
        }

        return IntPtr.Zero;
    }

    private bool OnPointerDown(Point devicePoint)
    {
        if (_dragging || !IsHit(devicePoint))
        {
            return false;
        }

        _gesture.Press(ToLogical(devicePoint), isSecondary: false);
        return true;
    }

    private bool OnPointerMove(Point devicePoint)
    {
        if (_gesture.Move(ToLogical(devicePoint)) == GestureEvent.DragStart && !_dragging)
        {
            _dragging = true;
            _window.DragMove();
            _dragging = false;
            SavePlacement();
            return true;
        }

        return false;
    }

    private bool OnPointerUp(Point devicePoint)
    {
        var gesture = _gesture.Release(ToLogical(devicePoint));
        if (gesture == GestureEvent.DragEnd)
        {
            SavePlacement();
        }
        return gesture != GestureEvent.None;
    }

    private bool IsHit(Point devicePoint) =>
        _controller.CurrentState is { } state
        && AlphaHitTestService.IsHit(
            ToLogical(devicePoint),
            state.Snapshot,
            state.ExpressionAssetId,
            state.Geometry);

    private Point ToLogical(Point devicePoint) => new(devicePoint.X / _dpi.X, devicePoint.Y / _dpi.Y);

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
    }

    private void SavePlacement()
    {
        var workDeviceX = _window.Left * _dpi.X;
        var workDeviceY = _window.Top * _dpi.Y;
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
            _window.Width,
            _window.Height));
    }
}