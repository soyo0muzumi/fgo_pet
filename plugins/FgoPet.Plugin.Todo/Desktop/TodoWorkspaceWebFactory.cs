using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Services;
using FgoPet.App.Theming;
using FgoPet.Core.Todo;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Todo.Desktop;

/// <summary>Builds the Web workspace while the existing WPF workspace remains the registered baseline.</summary>
public sealed class TodoWorkspaceWebFactory(TodoWebAdapter adapter, TodoChangeFeed changes,
    string storageRoot, ThemeService? theme = null, TimeProvider? clock = null)
{
    private readonly Dictionary<string, (TodoItem Snapshot, DateTimeOffset Until)> _undoTokens = [];
    private static readonly WebSurfacePolicy Policy = new("https://todo.fgopet.invalid/workspace/index.html",
        ["queryTasks", "getTask", "createTask", "updateTask", "deleteTask", "setCompletion", "undoCompletion"]);
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public TodoWebWorkspaceSurface CreateView()
    {
        var host = new WebView2SurfaceHost(Policy,
            Path.Combine(AppContext.BaseDirectory, "ui", "todo"),
            Path.Combine(storageRoot, "WebView2", "todo"), HandleCommandAsync);
        var subscription = changes.Subscribe(change => host.PostEvent(new
        {
            type = "todo.changed", revision = change.Revision,
        }));
        return new TodoWebWorkspaceSurface(host, subscription, theme);
    }

    internal ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(message.Type switch
            {
                "queryTasks" => new WebSurfaceCommandResult(true, adapter.QueryTasks(cancellationToken)),
                "getTask" => GetTask(message.Payload, cancellationToken),
                "createTask" => CreateTask(message.Payload, cancellationToken),
                "updateTask" => UpdateTask(message.Payload, cancellationToken),
                "deleteTask" => DeleteTask(message.Payload, cancellationToken),
                "setCompletion" => SetCompletion(message.Payload, cancellationToken),
                "undoCompletion" => UndoCompletion(message.Payload, cancellationToken),
                _ => new WebSurfaceCommandResult(false, ErrorCode: "TODO_UNKNOWN_COMMAND"),
            });
        }
        catch (OperationCanceledException) { throw; }
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

    private WebSurfaceCommandResult GetTask(JsonElement payload, CancellationToken token) =>
        ReadId(payload, out var id)
            ? adapter.GetTask(id, token) is { } item
                ? new(true, item)
                : new(false, ErrorCode: "TODO_NOT_FOUND")
            : new(false, ErrorCode: "TODO_INVALID_INPUT");

    private WebSurfaceCommandResult CreateTask(JsonElement payload, CancellationToken token)
    {
        if (!ReadText(payload, "title", 500, out var title)
            || !ReadOptionalText(payload, "description", 4000, out var description))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        return new(true, adapter.CreateTask(title, description, token));
    }

    private WebSurfaceCommandResult UpdateTask(JsonElement payload, CancellationToken token)
    {
        if (!ReadIdAndEtag(payload, out var id, out var etag)
            || !ReadText(payload, "title", 500, out var title)
            || !ReadOptionalText(payload, "description", 4000, out var description))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        IReadOnlyList<TodoStep>? steps = null;
        if (payload.TryGetProperty("steps", out var raw))
        {
            if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > 20)
                return new(false, ErrorCode: "TODO_INVALID_INPUT");
            try { steps = JsonSerializer.Deserialize<TodoStep[]>(raw.GetRawText(), PayloadJson); }
            catch (JsonException) { return new(false, ErrorCode: "TODO_INVALID_INPUT"); }
            if (steps is null) return new(false, ErrorCode: "TODO_INVALID_INPUT");
        }
        return new(true, adapter.UpdateTask(id, etag, title, description, steps, token));
    }

    private WebSurfaceCommandResult DeleteTask(JsonElement payload, CancellationToken token)
    {
        if (!ReadIdAndEtag(payload, out var id, out var etag))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        adapter.DeleteTask(id, etag, token);
        return new(true);
    }

    private WebSurfaceCommandResult SetCompletion(JsonElement payload, CancellationToken token)
    {
        if (!ReadIdAndEtag(payload, out var id, out var etag)
            || !payload.TryGetProperty("completed", out var completed)
            || completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        var confirm = payload.TryGetProperty("confirmIncompleteSteps", out var requested)
            && requested.ValueKind == JsonValueKind.True;
        try
        {
            var item = adapter.SetCompletion(id, completed.GetBoolean(), confirm, token, etag);
            if (!completed.GetBoolean()) return new(true, new { task = adapter.GetTask(item.Id, token) });
            var undoToken = Guid.NewGuid().ToString("N");
            foreach (var expired in _undoTokens.Where(entry =>
                         entry.Value.Until < (clock ?? TimeProvider.System).GetUtcNow())
                     .Select(entry => entry.Key).ToArray())
                _undoTokens.Remove(expired);
            _undoTokens[undoToken] = (item, (clock ?? TimeProvider.System).GetUtcNow().AddSeconds(8));
            return new(true, new { task = adapter.GetTask(item.Id, token), undoToken });
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(
            "Completing this Todo requires confirmation", StringComparison.Ordinal))
        {
            return new(false, ErrorCode: "TODO_CONFIRM_INCOMPLETE_STEPS");
        }
    }

    private WebSurfaceCommandResult UndoCompletion(JsonElement payload, CancellationToken token)
    {
        if (!ReadText(payload, "undoToken", 64, out var undoToken))
            return new(false, ErrorCode: "TODO_INVALID_INPUT");
        if (!_undoTokens.Remove(undoToken, out var undo)
            || (clock ?? TimeProvider.System).GetUtcNow() > undo.Until)
            return new(false, ErrorCode: "TODO_CONFLICT");
        return new(true, adapter.UndoCompletion(undo.Snapshot, token));
    }

    private static bool ReadId(JsonElement payload, out string id) =>
        ReadText(payload, "id", 128, out id);

    private static bool ReadIdAndEtag(JsonElement payload, out string id, out string etag)
    {
        if (!ReadId(payload, out id)) { etag = string.Empty; return false; }
        return ReadText(payload, "etag", 64, out etag) && etag.Length == 64;
    }

    private static bool ReadText(JsonElement payload, string name, int maxLength, out string value)
    {
        value = string.Empty;
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length is > 0 && value.Length <= maxLength;
    }

    private static bool ReadOptionalText(JsonElement payload, string name, int maxLength, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()?.Trim();
        return value is null || value.Length <= maxLength;
    }
}

public sealed class TodoWebWorkspaceSurface : Grid, IWorkspaceSurface
{
    private readonly WebView2SurfaceHost _host;
    private readonly IDisposable _subscription;
    private readonly ThemeService? _theme;
    private WorkspaceNavigation _navigation = new(WorkspaceNavigationKind.Overview);
    private long _themeVersion = 1;
    private bool _disposed;

    internal TodoWebWorkspaceSurface(WebView2SurfaceHost host, IDisposable subscription, ThemeService? theme)
    {
        _host = host;
        _subscription = subscription;
        _theme = theme;
        Children.Add(host);
        host.Ready += OnReady;
        if (theme is not null)
        {
            theme.ThemeChanged += OnThemeChanged;
            host.SetThemeVersion(_themeVersion, theme.CaptureWebPalette());
        }
        Unloaded += OnUnloaded;
    }

    internal WebView2SurfaceHost Host => _host;

    public void Navigate(WorkspaceNavigation navigation)
    {
        if (_disposed) return;
        _navigation = navigation;
        if (_host.State == WebSurfaceState.Ready) SendNavigation();
    }

    private void OnReady(object? sender, EventArgs e) => SendNavigation();
    private void SendNavigation() => _host.PostEvent(new
    {
        type = "workspace.navigate", kind = _navigation.Kind.ToString(), itemId = _navigation.ItemId,
    });

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_disposed || _theme is null) return;
        _host.SetThemeVersion(Interlocked.Increment(ref _themeVersion), _theme.CaptureWebPalette());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unloaded -= OnUnloaded;
        _host.Ready -= OnReady;
        if (_theme is not null) _theme.ThemeChanged -= OnThemeChanged;
        _subscription.Dispose();
        _host.Dispose();
    }
}
