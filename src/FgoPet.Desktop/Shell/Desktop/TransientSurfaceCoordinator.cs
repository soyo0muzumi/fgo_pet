using System.Windows;
using FgoPet.App.Main;
using FgoPet.Core.Geometry;
using FgoPet.Core.Windowing;
using FgoPet.UiSdk;

namespace FgoPet.App.Windowing;

/// <summary>Opens one plugin-owned transient surface beside the portrait at a time.</summary>
public sealed class TransientSurfaceCoordinator : IDisposable
{
    private readonly ITransientSurfaceCatalog _catalog;
    private readonly IScreenLayoutService _screen;
    private readonly Func<Window> _owner;
    private readonly Func<DeviceRect> _anchor;
    private readonly Action<string>? _diagnostics;
    private TransientSurfaceWindow? _window;
    private string? _surfaceId;
    private bool _disposed;

    public TransientSurfaceCoordinator(ITransientSurfaceCatalog catalog, IScreenLayoutService screen,
        Func<PortraitWindow> owner, Func<PortraitWindowCoordinator> portrait,
        Action<string>? diagnostics = null)
        : this(catalog, screen, () => owner(), () => portrait().PortraitDeviceBounds, diagnostics)
    {
    }

    internal TransientSurfaceCoordinator(ITransientSurfaceCatalog catalog, IScreenLayoutService screen,
        Func<Window> owner, Func<DeviceRect> anchor, Action<string>? diagnostics = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _diagnostics = diagnostics;
    }

    public bool IsOpen => _window?.IsVisible == true;

    public bool Toggle(string surfaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is not null && _surfaceId == surfaceId)
        {
            Close();
            return true;
        }
        Close();
        var descriptor = _catalog.TransientSurfaces.FirstOrDefault(item => item.Id == surfaceId);
        if (descriptor is null) return false;

        TransientSurfaceWindow? window = null;
        FrameworkElement? content = null;
        try
        {
            content = _catalog.CreateView(surfaceId);
            window = new TransientSurfaceWindow(_owner(), descriptor, content);
            Place(window, descriptor, _anchor());
            _window = window;
            _surfaceId = surfaceId;
            window.Closed += OnWindowClosed;
            window.Show();
            return true;
        }
        catch (Exception)
        {
            _diagnostics?.Invoke("TRANSIENT_OPEN_FAILED");
            if (window is not null) window.Close();
            else if (content is IDisposable disposable) disposable.Dispose();
            _window = null;
            _surfaceId = null;
            return false;
        }
    }

    public void Close()
    {
        var current = _window;
        _window = null;
        _surfaceId = null;
        if (current is not null)
        {
            current.Closed -= OnWindowClosed;
            current.Close();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _window)) return;
        _window = null;
        _surfaceId = null;
    }

    private void Place(TransientSurfaceWindow window, FgoPet.Extensibility.TransientSurfaceDescriptor descriptor,
        DeviceRect anchor)
    {
        var monitors = _screen.GetMonitors();
        var monitor = monitors.FirstOrDefault(item => anchor.X >= item.WorkArea.Left && anchor.X < item.WorkArea.Right
                && anchor.Y >= item.WorkArea.Top && anchor.Y < item.WorkArea.Bottom)
            ?? monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is null) throw new InvalidOperationException("TRANSIENT_NO_MONITOR");
        var dpi = _screen.GetDpi(monitor.Id);
        var bounds = TransientSurfacePlacement.Place(anchor, monitor.WorkArea, dpi,
            descriptor.PreferredWidthDip, descriptor.PreferredHeightDip);
        window.Left = bounds.X / dpi.X;
        window.Top = bounds.Y / dpi.Y;
        window.Width = bounds.Width / dpi.X;
        window.Height = bounds.Height / dpi.Y;
    }
}
