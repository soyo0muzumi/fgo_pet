using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Services;
using FgoPet.App.Theming;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Todo.Desktop;

/// <summary>Owns the Todo Peek page and its short-lived change subscriptions.</summary>
public sealed class TodoPeekSurfaceFactory : ITransientSurfaceViewFactory, IDisposable
{
    private static readonly WebSurfacePolicy Policy = new("https://todo.fgopet.invalid/peek/index.html",
        ["getPeekSnapshot", "quickAdd", "setCompletion", "openWorkspace"]);
    private readonly TodoWebAdapter _adapter;
    private readonly TodoChangeFeed _changes;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _openWorkspace;
    private readonly string _storageRoot;
    private readonly ThemeService? _theme;
    private readonly List<(WebView2SurfaceHost Host, IDisposable Subscription)> _views = [];
    private long _themeVersion = 1;
    private bool _disposed;

    public TodoPeekSurfaceFactory(TodoWebAdapter adapter, TodoChangeFeed changes, TimeProvider time,
        Func<string, bool> openWorkspace, string storageRoot, ThemeService? theme = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _openWorkspace = openWorkspace ?? throw new ArgumentNullException(nameof(openWorkspace));
        _storageRoot = storageRoot ?? throw new ArgumentNullException(nameof(storageRoot));
        _theme = theme;
        if (_theme is not null) _theme.ThemeChanged += OnThemeChanged;
    }

    public string SurfaceId => "todo.peek";

    public FrameworkElement CreateView()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var host = new WebView2SurfaceHost(Policy,
            Path.Combine(AppContext.BaseDirectory, "ui", "todo"),
            Path.Combine(_storageRoot, "WebView2", "todo"), HandleCommandAsync);
        if (_theme is not null) host.SetThemeVersion(_themeVersion, _theme.CaptureWebPalette());
        var subscription = _changes.Subscribe(change => host.PostEvent(new { type = "todo.changed", revision = change.Revision }));
        _views.Add((host, subscription));
        host.Disposed += OnHostDisposed;
        return host;
    }

    internal ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(message.Type switch
            {
                "getPeekSnapshot" => new WebSurfaceCommandResult(true,
                    _adapter.GetPeekSnapshot(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                        _time.GetUtcNow(), TimeZoneInfo.Local).DateTime), TimeZoneInfo.Local, cancellationToken)),
                "quickAdd" => QuickAdd(message.Payload, cancellationToken),
                "setCompletion" => SetCompletion(message.Payload, cancellationToken),
                "openWorkspace" => _openWorkspace("todo.workspace")
                    ? new WebSurfaceCommandResult(true)
                    : new WebSurfaceCommandResult(false, ErrorCode: "TODO_WORKSPACE_UNAVAILABLE"),
                _ => new WebSurfaceCommandResult(false, ErrorCode: "TODO_UNKNOWN_COMMAND"),
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException error) when (error.Message.StartsWith(
            "Completing this Todo requires confirmation", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "TODO_CONFIRM_INCOMPLETE_STEPS"));
        }
        catch (KeyNotFoundException)
        {
            return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "TODO_NOT_FOUND"));
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "TODO_INVALID_INPUT"));
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "TODO_CONFLICT"));
        }
    }

    private WebSurfaceCommandResult QuickAdd(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryReadString(payload, "title", 500, out var title))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        var item = _adapter.QuickAdd(title, cancellationToken);
        return new(true, new { item.Id });
    }

    private WebSurfaceCommandResult SetCompletion(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryReadString(payload, "id", 128, out var id)
            || !TryReadString(payload, "etag", 64, out var etag)
            || !payload.TryGetProperty("completed", out var completion)
            || completion.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        var confirm = payload.TryGetProperty("confirmIncompleteSteps", out var confirmed)
            && confirmed.ValueKind == JsonValueKind.True;
        var item = _adapter.SetCompletion(id, completion.GetBoolean(), confirm, cancellationToken, etag);
        return new(true, new { item.Id, item.Status });
    }

    private static bool TryReadString(JsonElement payload, string name, int limit, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 && value.Length <= limit;
    }

    private void OnHostDisposed(object? sender, EventArgs e)
    {
        if (sender is not WebView2SurfaceHost host) return;
        host.Disposed -= OnHostDisposed;
        var index = _views.FindIndex(view => ReferenceEquals(view.Host, host));
        if (index < 0) return;
        _views[index].Subscription.Dispose();
        _views.RemoveAt(index);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed || _theme is null) return;
        var version = Interlocked.Increment(ref _themeVersion);
        var variables = _theme.CaptureWebPalette();
        foreach (var view in _views.ToArray()) view.Host.SetThemeVersion(version, variables);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged;
        foreach (var view in _views.ToArray()) view.Host.Dispose();
        foreach (var view in _views) view.Subscription.Dispose();
        _views.Clear();
    }
}
