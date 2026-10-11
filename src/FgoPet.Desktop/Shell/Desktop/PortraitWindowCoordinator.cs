using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FgoPet.App.Main;
using FgoPet.App.Panels;
using FgoPet.UiSdk;
using FgoPet.Core.Geometry;
using FgoPet.Core.Panels;
using FgoPet.Core.Portraits;
using FgoPet.Core.Windowing;
using Point = System.Windows.Point;

namespace FgoPet.App.Windowing;

/// <summary>
/// Wires a portrait window to the controller: presents validated states, centers the
/// first portrait shown in a process, saves later placement, makes transparent pixels
/// pass through via <c>WM_NCHITTEST</c>, and turns press/move/release into click/drag
/// using the system drag threshold.
/// </summary>
public sealed class PortraitWindowCoordinator : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const int WmDpiChanged = 0x02E0;
    private const int WmHotkey = 0x0312;
    private const int ChatHotkeyId = 0x4D47;
    private bool _chatHotkeyRegistered;
    private HwndSource? _shortcutSource;
    public event Action? GeometryChanged;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    private const nint HtTransparent = -1;
    private const nint HtClient = 1;

    private readonly PortraitWindow _window;
    private readonly IPortraitSurface _controller;
    private readonly IWindowPlacementStore _placement;
    private readonly IScreenLayoutService _screen;
    private readonly PointerGestureRecognizer _gesture = new();
    private HwndSource? _source;
    private Dpi2 _dpi = new(1.0, 1.0);
    private bool _dragging;
    private bool _pressWasOnPortrait;
    private bool _initialPlacementRequested;
    private bool _initialPlacementApplied;
    private bool _disposed;
    private FrameworkElement? _view;

    public PortraitWindowCoordinator(
        PortraitWindow window,
        IPortraitSurface controller,
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
            if (InteractiveSurface.Contains(e.OriginalSource as DependencyObject))
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

    /// <summary>
    /// Requests deterministic placement for the first portrait shown in this process.
    /// Persisted placement is intentionally ignored; once applied, hiding and showing
    /// the portrait again keeps its current location.
    /// </summary>
    public void InitializePlacement()
    {
        _initialPlacementRequested = true;
        TryCenterInitialPortrait();
    }

    internal void InitializeChatShortcut()
    {
        if (_shortcutSource is not null || _disposed) return;
        _shortcutSource = new HwndSource(new HwndSourceParameters("FgoPet.Margin.ChatEntry")
        { ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _shortcutSource.AddHook(OnWindowMessage);
        _chatHotkeyRegistered = RegisterHotKey(_shortcutSource.Handle, ChatHotkeyId, 0x4003, 0x20);
        if (!_chatHotkeyRegistered) System.Diagnostics.Trace.TraceWarning("MARGIN_CHAT_SHORTCUT_UNAVAILABLE");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.StateChanged -= OnControllerStateChanged;
        _window.AttachedPanel.PropertyChanged -= OnPanelPropertyChanged;
        _source?.RemoveHook(OnWindowMessage);
        if (_chatHotkeyRegistered && _shortcutSource is not null) UnregisterHotKey(_shortcutSource.Handle, ChatHotkeyId);
        _shortcutSource?.RemoveHook(OnWindowMessage);
        _shortcutSource?.Dispose();
    }

    /// <summary>Current portrait pixel bounds on screen; used for dialogue placement.</summary>
    internal DeviceRect PortraitDeviceBounds
    {
        get
        {
            var portrait = _window.PortraitScreenBounds;
            var dpi = IsValidDpi(_dpi) ? _dpi : new Dpi2(1.0, 1.0);
            var offset = _window.PortraitHostOffset;
            var origin = _source is not null
                ? _window.PointToScreen(new Point(offset.X, offset.Y))
                : new Point(portrait.X * dpi.X, portrait.Y * dpi.Y);
            return new DeviceRect(
                (int)Math.Round(origin.X),
                (int)Math.Round(origin.Y),
                Math.Max(1, (int)Math.Round(portrait.Width * dpi.X)),
                Math.Max(1, (int)Math.Round(portrait.Height * dpi.Y)));
        }
    }

    private void AttachHook()
    {
        var dpi = VisualTreeHelper.GetDpi(_window);
        ApplyWindowDpi(new Dpi2(dpi.DpiScaleX, dpi.DpiScaleY));
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnWindowMessage);
        InitializeChatShortcut();
        TryCenterInitialPortrait();
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmHotkey when wParam.ToInt32() == ChatHotkeyId:
                _window.HandlePortraitClick();
                handled = true;
                break;
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A: // WM_SETTINGCHANGE (including work area / taskbar)
                _window.Dispatcher.BeginInvoke(new Action(ClampPortraitToWorkArea));
                break;
            case WmNcHitTest:
                var nativePoint = new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                if (_window.ManagementHitTest?.Invoke(nativePoint) == true)
                {
                    handled = true;
                    return HtTransparent;
                }
                var state = _controller.CurrentFrame;
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
                return state.IsHit(_window.ToPortraitLocal(logical))
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
        if (_controller.CurrentFrame is not null)
        {
            ArrangeAttachedPanel();
            GeometryChanged?.Invoke();
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
            if (_controller is IPortraitTapSurface tapSurface) tapSurface.OnPortraitTap();
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
        _controller.CurrentFrame is { } state
        && state.IsHit(_window.ToPortraitLocal(windowPoint));

    internal void ClampPortraitToWorkArea()
    {
        var device = PortraitDeviceBounds;
        var monitor = SelectNearestMonitor(device, _screen.GetMonitors());
        if (monitor is null)
        {
            return;
        }

        var clamped = ScreenLayout.ClampFullyVisible(device, monitor.WorkArea);
        _window.MovePortraitToDevice(new DevicePoint(clamped.X, clamped.Y), _dpi);
        ArrangeAttachedPanel();
        GeometryChanged?.Invoke();
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

    private void ApplyCurrentState()
    {
        if (_disposed) return;
        var state = _controller.CurrentFrame;
        if (state is null)
        {
            _window.Hide();
            return;
        }

        _view ??= _controller.CreateView();
        state.Present(_view);
        _window.Present(_view, state.Geometry);
        ArrangeAttachedPanel();
        TryCenterInitialPortrait();
    }

    private void TryCenterInitialPortrait()
    {
        if (!_initialPlacementRequested || _initialPlacementApplied || _source is null)
        {
            return;
        }

        var monitors = _screen.GetMonitors();
        var monitor = monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors.FirstOrDefault();
        if (monitor is null)
        {
            return;
        }

        var monitorDpi = _screen.GetDpi(monitor.Id);
        if (IsValidDpi(monitorDpi)
            && (Math.Abs(_dpi.X - monitorDpi.X) > 0.001 || Math.Abs(_dpi.Y - monitorDpi.Y) > 0.001))
        {
            ApplyWindowDpi(monitorDpi);
        }

        var portrait = _window.PortraitScreenBounds;
        if (!double.IsFinite(portrait.Width) || portrait.Width <= 0
            || !double.IsFinite(portrait.Height) || portrait.Height <= 0
            || !IsValidDpi(_dpi))
        {
            return;
        }

        var deviceSize = new DeviceSize(
            Math.Max(1, (int)Math.Round(portrait.Width * _dpi.X)),
            Math.Max(1, (int)Math.Round(portrait.Height * _dpi.Y)));
        var centered = new DeviceRect(
            monitor.WorkArea.X + ((monitor.WorkArea.Width - deviceSize.Width) / 2),
            monitor.WorkArea.Y + ((monitor.WorkArea.Height - deviceSize.Height) / 2),
            deviceSize.Width,
            deviceSize.Height);
        var visible = ScreenLayout.ClampFullyVisible(centered, monitor.WorkArea);
        _window.MovePortraitToDevice(new DevicePoint(visible.X, visible.Y), _dpi);
        ArrangeAttachedPanel();
        _initialPlacementApplied = true;
    }

    private static bool IsValidDpi(Dpi2 dpi) =>
        double.IsFinite(dpi.X) && dpi.X > 0
        && double.IsFinite(dpi.Y) && dpi.Y > 0;

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var relayout = e.PropertyName == nameof(AttachedPanelViewModel.State)
            || e.PropertyName == nameof(AttachedPanelViewModel.IsCompactSurfaceActive)
            || e.PropertyName == nameof(AttachedPanelViewModel.BlocksAutoCollapse);

        if (relayout && (_window.AttachedPanel.State != AttachedPanelState.Collapsed || _window.AttachedPanel.IsCompactSurfaceActive))
        {
            // State switches and the compact message→timer body swap both change the
            // panel height budget; re-arranging keeps portrait and panel anchored.
            ArrangeAttachedPanel();
        }
    }

    private void OnControllerStateChanged(object? sender, EventArgs e)
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted) _window.Dispatcher.BeginInvoke(ApplyCurrentState);
    }

    private void ArrangeAttachedPanel()
    {
        if (_controller.CurrentFrame is not { } state)
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
            if (_window.AttachedPanel.State == AttachedPanelState.Collapsed)
            {
                _window.PrepareStablePanelLayout(state.Geometry, monitor.WorkArea, _dpi);
            }
            else
            {
                _window.ArrangeOverlayPanel(state.Geometry, monitor.WorkArea, _dpi);
            }
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
