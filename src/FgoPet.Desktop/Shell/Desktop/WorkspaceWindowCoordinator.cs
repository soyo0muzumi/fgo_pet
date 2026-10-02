using System.Windows;
using FgoPet.UiSdk;

namespace FgoPet.App.Windowing;

/// <summary>Hosts a capability workspace outside the conversation window.</summary>
public sealed class WorkspaceWindowCoordinator : IWorkspaceLauncher, IDisposable
{
    private readonly IWorkspaceCatalog _catalog;
    private readonly Func<Window> _owner;
    private readonly Dictionary<string, (Window Window, FrameworkElement View)> _windows = new();
    private bool _disposed;

    public WorkspaceWindowCoordinator(IWorkspaceCatalog catalog, Func<Window> owner)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public bool Open(string workspaceId) => Open(workspaceId, new(WorkspaceNavigationKind.Overview));

    public bool Open(string workspaceId, WorkspaceNavigation navigation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windows.TryGetValue(workspaceId, out var existing))
        {
            try
            {
                if (existing.View is IWorkspaceSurface surface) surface.Navigate(navigation);
                existing.Window.Activate();
                return true;
            }
            catch (Exception) { return false; }
        }
        var descriptor = _catalog.Workspaces.FirstOrDefault(item => item.Id == workspaceId);
        if (descriptor is null) return false;

        FrameworkElement? view = null;
        try
        {
            view = _catalog.CreateView(workspaceId);
            var window = new Window
            {
                Owner = _owner(), Title = descriptor.Title, Content = view,
                Width = 920, Height = 660, MinWidth = 520, MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            window.SetResourceReference(Window.BackgroundProperty, "Surface.Content");
            window.Closed += (_, _) =>
            {
                _windows.Remove(workspaceId);
                if (view is IDisposable disposable) disposable.Dispose();
            };
            _windows.Add(workspaceId, (window, view));
            window.Show();
            if (view is IWorkspaceSurface surface)
                surface.Navigate(navigation);
            return true;
        }
        catch (Exception)
        {
            if (_windows.Remove(workspaceId, out var failed)) failed.Window.Close();
            else if (view is IDisposable disposable) disposable.Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _windows.Values.ToArray()) entry.Window.Close();
        _windows.Clear();
    }
}
