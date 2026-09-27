using System.Windows;
using FgoPet.App.Services;
using FgoPet.App.Views;
using FgoPet.Extensibility;
using FgoPet.Plugin.Todo;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Todo.Desktop;

public sealed class TodoDesktopPlugin(TodoPlugin conversation, TodoApplicationService todos)
    : IFgoPetPlugin, IWorkspaceViewFactory, IDisposable
{
    private readonly List<TodoWorkspaceView> _surfaces = [];
    private volatile bool _closed;
    public PluginManifest Manifest => conversation.Manifest;
    public PluginContributions Contributions => conversation.Contributions;
    public string WorkspaceId => "todo.workspace";
    public FrameworkElement CreateView()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var view = new TodoWorkspaceView(todos);
        _surfaces.Add(view);
        return view;
    }
    public ValueTask StartAsync(CancellationToken stoppingToken) => conversation.StartAsync(stoppingToken);
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _closed = true;
        await conversation.StopAsync(cancellationToken);
        foreach (var surface in _surfaces.ToArray()) await surface.DisposeAsync();
        _surfaces.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await conversation.DisposeAsync();
    }
    public void Dispose()
    {
        _closed = true;
        conversation.Dispose();
        foreach (var surface in _surfaces) surface.Dispose();
        _surfaces.Clear();
    }
}
