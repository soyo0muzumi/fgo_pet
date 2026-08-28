using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FgoPet.App.Focus;
using FgoPet.App.Panels;
using FgoPet.App.Portraits;
using FgoPet.Core.Geometry;
using FgoPet.Core.Panels;

namespace FgoPet.App.Main;

/// <summary>
/// Transparent, borderless, always-on-top portrait host window. The exact DPI,
/// placement, hit-testing, and lifecycle behavior is completed in Tasks 8-10.
/// </summary>
public partial class PortraitWindow : Window
{
    private readonly AttachedPanelViewModel _panel;
    private readonly AttachedPanelView _panelView;
    private readonly IFocusSessionService? _focus;
    private readonly DispatcherTimer _idleTimer;
    private PortraitGeometry? _geometry;
    private double _portraitOffsetX;
    private double _portraitOffsetY;
    private bool _stablePanelLayoutPrepared;

    public PortraitWindow() : this(new AttachedPanelViewModel(TimeProvider.System))
    {
    }

    public PortraitWindow(AttachedPanelViewModel panel) : this(panel, focus: null)
    {
    }

    public PortraitWindow(AttachedPanelViewModel panel, IFocusSessionService? focus)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _focus = focus;
        InitializeComponent();
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _idleTimer.Tick += (_, _) => HandlePanelIdleTick();
        _panelView = new AttachedPanelView { DataContext = panel };
        PanelHost.Content = _panelView;
        panel.PropertyChanged += OnPanelPropertyChanged;
        Loaded += (_, _) => _idleTimer.Start();
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            panel.PropertyChanged -= OnPanelPropertyChanged;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HandleEscape();
                e.Handled = true;
            }
        };
        ApplyPanelState();
    }

    public PortraitView PortraitView => Portrait;

    internal AttachedPanelViewModel AttachedPanel => _panel;

    internal bool IsAttachedPanelVisible => PanelHost.Visibility == Visibility.Visible;

    internal LogicalRect PortraitScreenBounds => new(
        Left + _portraitOffsetX,
        Top + _portraitOffsetY,
        _geometry?.LogicalSize.Width ?? Width,
        _geometry?.LogicalSize.Height ?? Height);

    internal void HandlePortraitClick() => _panel.PortraitClick();

    internal void HandleEscape() => _panel.Escape();

    internal void HandlePanelIdleTick()
    {
        // The focus countdown consumes the same 1 s cadence as the idle collapse.
        _focus?.Tick();
        _panel.Tick();
    }

    internal bool IsAttachedPanelHit(Point logicalPoint)
    {
        if (!IsAttachedPanelVisible)
        {
            return false;
        }

        var left = Canvas.GetLeft(PanelHost);
        var top = Canvas.GetTop(PanelHost);
        var width = PanelHost.ActualWidth > 0 ? PanelHost.ActualWidth : PanelHost.DesiredSize.Width;
        var height = PanelHost.ActualHeight > 0 ? PanelHost.ActualHeight : Math.Min(PanelHost.DesiredSize.Height, PanelHost.MaxHeight);
        return logicalPoint.X >= left && logicalPoint.X < left + width
            && logicalPoint.Y >= top && logicalPoint.Y < top + height;
    }

    internal Point ToPortraitLocal(Point windowPoint) => new(
        windowPoint.X - _portraitOffsetX,
        windowPoint.Y - _portraitOffsetY);

    internal void MovePortraitToDevice(DevicePoint location, Dpi2 dpi)
    {
        Left = (location.X / dpi.X) - _portraitOffsetX;
        Top = (location.Y / dpi.Y) - _portraitOffsetY;
    }

    /// <summary>Loads a validated snapshot at the given window/portrait geometry.</summary>
    public void Present(PortraitSnapshot snapshot, PortraitGeometry geometry)
    {
        _stablePanelLayoutPrepared = false;
        _geometry = geometry;
        Portrait.Load(snapshot, geometry);
        Portrait.Width = geometry.LogicalSize.Width;
        Portrait.Height = geometry.LogicalSize.Height;
        if (_panel.State == AttachedPanelState.Collapsed)
        {
            CollapsePanelLayout();
        }
    }

    internal DeviceRect ArrangeOverlayPanel(PortraitGeometry geometry, DeviceRect workArea, Dpi2 dpi)
    {
        return ArrangeStablePanelLayout(geometry, workArea, dpi);
    }

    internal void PrepareStablePanelLayout(PortraitGeometry geometry, DeviceRect workArea, Dpi2 dpi)
    {
        ArrangeStablePanelLayout(geometry, workArea, dpi);
    }

    private DeviceRect ArrangeStablePanelLayout(PortraitGeometry geometry, DeviceRect workArea, Dpi2 dpi)
    {
        _geometry = geometry;
        var windowLeft = double.IsFinite(Left) ? Left : 0;
        var windowTop = double.IsFinite(Top) ? Top : 0;
        var portraitLeft = (int)Math.Round((windowLeft + _portraitOffsetX) * dpi.X);
        var portraitTop = (int)Math.Round((windowTop + _portraitOffsetY) * dpi.Y);
        var portraitBounds = new DeviceRect(
            portraitLeft,
            portraitTop,
            geometry.DeviceSize.Width,
            geometry.DeviceSize.Height);
        var anchor = new DevicePoint(
            portraitLeft + geometry.PanelAnchorDevice.X,
            portraitTop + geometry.PanelAnchorDevice.Y);

        var panelWidthDip = Math.Clamp(geometry.LogicalSize.Width * (440.0 / 303.0), 220, 340);
        var compactHeightDip = panelWidthDip * (160.0 / 440.0);
        // Only Compact/Collapsed keep the compact height; all four expanded states
        // share the reserved stretch height so opening a column never moves the portrait.
        var panelHeightDip = AttachedPanelStateMachine.IsExpanded(_panel.State)
            ? Math.Min(280, workArea.Height * 0.6 / dpi.Y)
            : compactHeightDip;
        var reservedHeightDip = Math.Min(280, workArea.Height * 0.6 / dpi.Y);
        PanelHost.Width = panelWidthDip;
        PanelHost.Height = panelHeightDip;
        PanelHost.MaxHeight = panelHeightDip;
        _panelView.ApplyPhase0Clip(panelWidthDip, panelHeightDip, panelWidthDip / 22.0);
        PanelHost.Measure(new Size(panelWidthDip, panelHeightDip));
        var desired = new DeviceSize(
            Math.Max(1, (int)Math.Ceiling(panelWidthDip * dpi.X)),
            Math.Max(1, (int)Math.Ceiling(panelHeightDip * dpi.Y)));
        var panelWidth = Math.Min(desired.Width, workArea.Width);
        var panelHeight = Math.Min(desired.Height, (int)Math.Floor(workArea.Height * 0.6));
        var reservedHeight = Math.Min(
            Math.Max(1, (int)Math.Ceiling(reservedHeightDip * dpi.Y)),
            (int)Math.Floor(workArea.Height * 0.6));
        var panelBounds = new DeviceRect(
            Math.Clamp(anchor.X - (panelWidth / 2), workArea.Left, workArea.Right - panelWidth),
            Math.Clamp(anchor.Y, workArea.Top, workArea.Bottom - panelHeight),
            panelWidth,
            panelHeight);
        var reservedPanelBounds = new DeviceRect(
            panelBounds.Left,
            Math.Clamp(anchor.Y, workArea.Top, workArea.Bottom - reservedHeight),
            panelWidth,
            reservedHeight);
        PanelHost.MaxHeight = panelBounds.Height / dpi.Y;

        var hostLeft = Math.Min(portraitBounds.Left, reservedPanelBounds.Left);
        var hostTop = Math.Min(portraitBounds.Top, reservedPanelBounds.Top);
        var hostRight = Math.Max(portraitBounds.Right, reservedPanelBounds.Right);
        var hostBottom = Math.Max(portraitBounds.Bottom, reservedPanelBounds.Bottom);
        _portraitOffsetX = (portraitBounds.Left - hostLeft) / dpi.X;
        _portraitOffsetY = (portraitBounds.Top - hostTop) / dpi.Y;
        Canvas.SetLeft(Portrait, _portraitOffsetX);
        Canvas.SetTop(Portrait, _portraitOffsetY);
        Canvas.SetLeft(PanelHost, (panelBounds.Left - hostLeft) / dpi.X);
        Canvas.SetTop(PanelHost, (panelBounds.Top - hostTop) / dpi.Y);
        Left = hostLeft / dpi.X;
        Top = hostTop / dpi.Y;
        Width = HostCanvas.Width = (hostRight - hostLeft) / dpi.X;
        Height = HostCanvas.Height = (hostBottom - hostTop) / dpi.Y;
        _stablePanelLayoutPrepared = true;
        return panelBounds;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AttachedPanelViewModel.State))
        {
            ApplyPanelState();
        }
    }

    private void ApplyPanelState()
    {
        PanelHost.Visibility = _panel.State == AttachedPanelState.Collapsed
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_panel.State == AttachedPanelState.Collapsed && _geometry is not null && !_stablePanelLayoutPrepared)
        {
            CollapsePanelLayout();
        }
    }

    private void CollapsePanelLayout()
    {
        var geometry = _geometry!;
        var portraitLeft = Left + _portraitOffsetX;
        var portraitTop = Top + _portraitOffsetY;
        _portraitOffsetX = 0;
        _portraitOffsetY = 0;
        Canvas.SetLeft(Portrait, 0);
        Canvas.SetTop(Portrait, 0);
        Left = portraitLeft;
        Top = portraitTop;
        Width = HostCanvas.Width = geometry.LogicalSize.Width;
        Height = HostCanvas.Height = geometry.LogicalSize.Height;
    }
}
