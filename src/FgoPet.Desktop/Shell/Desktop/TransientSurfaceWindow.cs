using System.Windows;
using System.Windows.Input;
using FgoPet.Extensibility;

namespace FgoPet.App.Windowing;

/// <summary>Shell-owned transient window; feature content stays in its own view.</summary>
public sealed class TransientSurfaceWindow : Window
{
    private readonly FrameworkElement _surface;
    private readonly Window _owner;
    private bool _closing;
    private bool _closed;

    public TransientSurfaceWindow(Window owner, TransientSurfaceDescriptor descriptor, FrameworkElement surface)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        ArgumentNullException.ThrowIfNull(descriptor);
        Owner = owner;
        Title = descriptor.Title;
        Width = descriptor.PreferredWidthDip;
        Height = descriptor.PreferredHeightDip;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = owner.Topmost;
        Content = surface;
        SetResourceReference(BackgroundProperty, "Surface.Content");
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        Closed += OnClosed;
        owner.Closed += OnOwnerClosed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_closing) return;
        _closing = true;
        Close();
        if (_owner.IsVisible) _owner.Activate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _closing = true;
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_closing && !_closed) Close();
    }
    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        if (!_closing && !_closed) Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        PreviewKeyDown -= OnPreviewKeyDown;
        Deactivated -= OnDeactivated;
        Closing -= OnClosing;
        Closed -= OnClosed;
        _owner.Closed -= OnOwnerClosed;
        if (_surface is IDisposable disposable) disposable.Dispose();
    }
}
