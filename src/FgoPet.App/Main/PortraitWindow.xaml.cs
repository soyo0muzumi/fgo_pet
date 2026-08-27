using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _idleTimer;
    private PortraitGeometry? _geometry;
    private Dpi2 _dpi = new(1, 1);
    private double _portraitOffsetX;
    private double _portraitOffsetY;

    public PortraitWindow() : this(new AttachedPanelViewModel(TimeProvider.System))
    {
    }

    public PortraitWindow(AttachedPanelViewModel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        InitializeComponent();
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _idleTimer.Tick += (_, _) => HandlePanelIdleTick();
        PanelHost.Content = new AttachedPanelView { DataContext = panel };
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

    internal void HandlePanelIdleTick() => _panel.Tick();

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

    /// <summary>Loads a validated snapshot at the given window/portrait geometry.</summary>
    public void Present(PortraitSnapshot snapshot, PortraitGeometry geometry)
    {
        _geometry = geometry;
        Portrait.Load(snapshot, geometry);
        Portrait.Width = geometry.LogicalSize.Width;
        Portrait.Height = geometry.LogicalSize.Height;
        if (_panel.State == AttachedPanelState.Collapsed)
        {
            CollapsePanelLayout();
        }
    }

    internal PanelPlacement ArrangeAttachedPanel(PortraitGeometry geometry, DeviceRect workArea, Dpi2 dpi)
    {
        _geometry = geometry;
        _dpi = dpi;
        var portraitLeft = (int)Math.Round((Left + _portraitOffsetX) * dpi.X);
        var portraitTop = (int)Math.Round((Top + _portraitOffsetY) * dpi.Y);
        var portraitBounds = new DeviceRect(
            portraitLeft,
            portraitTop,
            geometry.DeviceSize.Width,
            geometry.DeviceSize.Height);
        var anchor = new DevicePoint(
            portraitLeft + geometry.PanelAnchorDevice.X,
            portraitTop + geometry.PanelAnchorDevice.Y);

        PanelHost.Measure(new Size(340, double.PositiveInfinity));
        var desired = new DeviceSize(
            Math.Max(1, (int)Math.Ceiling(PanelHost.DesiredSize.Width * dpi.X)),
            Math.Max(1, (int)Math.Ceiling(PanelHost.DesiredSize.Height * dpi.Y)));
        var placement = AttachedPanelLayout.Place(anchor, desired, workArea, portraitBounds);
        PanelHost.MaxHeight = placement.Bounds.Height / dpi.Y;

        var hostLeft = Math.Min(portraitBounds.Left, placement.Bounds.Left);
        var hostTop = Math.Min(portraitBounds.Top, placement.Bounds.Top);
        var hostRight = Math.Max(portraitBounds.Right, placement.Bounds.Right);
        var hostBottom = Math.Max(portraitBounds.Bottom, placement.Bounds.Bottom);
        _portraitOffsetX = (portraitBounds.Left - hostLeft) / dpi.X;
        _portraitOffsetY = (portraitBounds.Top - hostTop) / dpi.Y;
        Canvas.SetLeft(Portrait, _portraitOffsetX);
        Canvas.SetTop(Portrait, _portraitOffsetY);
        Canvas.SetLeft(PanelHost, (placement.Bounds.Left - hostLeft) / dpi.X);
        Canvas.SetTop(PanelHost, (placement.Bounds.Top - hostTop) / dpi.Y);
        Left = hostLeft / dpi.X;
        Top = hostTop / dpi.Y;
        Width = HostCanvas.Width = (hostRight - hostLeft) / dpi.X;
        Height = HostCanvas.Height = (hostBottom - hostTop) / dpi.Y;
        return placement;
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
        if (_panel.State == AttachedPanelState.Collapsed && _geometry is not null)
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
