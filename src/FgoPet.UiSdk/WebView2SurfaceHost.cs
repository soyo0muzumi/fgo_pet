using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FgoPet.UiSdk;

public enum WebSurfaceState { Initializing, Ready, Failed, Closed }
public sealed record WebSurfaceCommandResult(bool Success, object? Payload = null, string? ErrorCode = null);

/// <summary>Hosts trusted local assets behind a restricted origin and typed command handler.</summary>
public sealed class WebView2SurfaceHost : Grid, IDisposable
{
    private static readonly JsonSerializerOptions MessageJson = CreateMessageJson();
    private readonly WebSurfacePolicy _policy;
    private readonly string _assetsDirectory;
    private readonly string _profileDirectory;
    private readonly Func<WebSurfaceMessage, CancellationToken, ValueTask<WebSurfaceCommandResult>> _commandHandler;
    private readonly Action<string>? _diagnostics;
    private readonly WebView2 _webView = new();
    private readonly TextBlock _fallback = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private bool _disposed;
    private long _themeVersion;
    private long _ackedThemeVersion;
    private long _watchedThemeVersion;
    private IReadOnlyDictionary<string, string> _themeVariables = new Dictionary<string, string>();

    public WebView2SurfaceHost(WebSurfacePolicy policy, string assetsDirectory, string profileDirectory,
        Func<WebSurfaceMessage, CancellationToken, ValueTask<WebSurfaceCommandResult>> commandHandler,
        Action<string>? diagnostics = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _assetsDirectory = Path.GetFullPath(assetsDirectory ?? throw new ArgumentNullException(nameof(assetsDirectory)));
        _profileDirectory = Path.GetFullPath(profileDirectory ?? throw new ArgumentNullException(nameof(profileDirectory)));
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        _diagnostics = diagnostics;
        Children.Add(_webView);
        Children.Add(_fallback);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public WebSurfaceState State { get; private set; } = WebSurfaceState.Initializing;
    public event EventHandler? Disposed;
    public event EventHandler? Ready;
    public string? ErrorCode { get; private set; }
    public long AckedThemeVersion => _ackedThemeVersion;
    public bool ThemeAckTimedOut { get; private set; }

    public Task InitializeAsync()
    {
        if (_disposed) return Task.CompletedTask;
        return _initialization ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        if (!Directory.Exists(_assetsDirectory))
        {
            Fail("WEB_ASSETS_MISSING");
            return;
        }

        try
        {
            Directory.CreateDirectory(_profileDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileDirectory);
            if (_disposed) return;
            await _webView.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            var core = _webView.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping(_policy.DocumentHost, _assetsDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
            core.Navigate(_policy.DocumentUrl);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed) Fail("WEB_INIT_FAILED");
        }
    }

    public void SetThemeVersion(long version, IReadOnlyDictionary<string, string> variables)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetThemeVersion(version, variables)));
            return;
        }
        if (_disposed || version <= _themeVersion) return;
        _themeVersion = version;
        ThemeAckTimedOut = false;
        _themeVariables = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        if (State == WebSurfaceState.Ready) SendTheme();
    }

    public void PostEvent(object message)
    {
        if (_disposed || State != WebSurfaceState.Ready) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => PostEvent(message)));
            return;
        }
        if (_disposed || State != WebSurfaceState.Ready) return;
        _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, MessageJson));
    }

    public void Dispose()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.Invoke(Dispose);
            return;
        }
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DetachCoreHandlers();
        try
        {
            _webView.Dispose();
        }
        catch (Exception)
        {
            // A crashed or already torn-down browser process can make disposal throw; a closing
            // window must never surface that exception to the shell.
        }
        _lifetime.Dispose();
        State = WebSurfaceState.Closed;
        Disposed?.Invoke(this, EventArgs.Empty);
    }

    private void DetachCoreHandlers()
    {
        try
        {
            if (_webView.CoreWebView2 is not { } core) return;
            core.NavigationStarting -= OnNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.PermissionRequested -= OnPermissionRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.ProcessFailed -= OnProcessFailed;
        }
        catch (Exception)
        {
            // Reading CoreWebView2 throws once the browser process has crashed.
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = InitializeAsync();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_policy.AllowsNavigation(e.Uri)) e.Cancel = true;
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The first-party surfaces do not use frames.
        e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;
    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = CoreWebView2PermissionState.Deny;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess && !_disposed) Fail("WEB_NAVIGATION_FAILED");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // A crashed browser process must surface as the fallback panel, not as a dead-but-Ready surface.
        if (_disposed || State is WebSurfaceState.Failed or WebSurfaceState.Closed) return;
        Fail("WEB_PROCESS_FAILED");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || State is WebSurfaceState.Failed or WebSurfaceState.Closed) return;
        var message = _policy.ValidateMessage(e.Source, _webView.CoreWebView2?.Source, e.WebMessageAsJson);
        if (message is null)
        {
            _diagnostics?.Invoke("WEB_MESSAGE_REJECTED");
            return;
        }
        if (message.Type == "ready")
        {
            if (State != WebSurfaceState.Ready)
            {
                State = WebSurfaceState.Ready;
                if (_themeVersion > 0) SendTheme();
                Ready?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        if (message.Type == "theme.ack")
        {
            if (message.Payload.TryGetProperty("version", out var version)
                && version.TryGetInt64(out var acknowledged) && acknowledged == _themeVersion)
            {
                _ackedThemeVersion = acknowledged;
                ThemeAckTimedOut = false;
            }
            return;
        }
        if (State == WebSurfaceState.Ready) _ = HandleCommandAsync(message);
    }

    private async Task HandleCommandAsync(WebSurfaceMessage message)
    {
        try
        {
            var result = await _commandHandler(message, _lifetime.Token);
            if (_disposed || _lifetime.IsCancellationRequested) return;
            _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "command.result", requestId = message.RequestId, success = result.Success,
                payload = result.Payload, errorCode = result.ErrorCode,
            }, MessageJson));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_disposed) return;
            _diagnostics?.Invoke("WEB_COMMAND_FAILED");
            _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "command.result", requestId = message.RequestId, success = false, errorCode = "WEB_COMMAND_FAILED",
            }, MessageJson));
        }
    }

    private void SendTheme()
    {
        if (_disposed || State != WebSurfaceState.Ready) return;
        _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "theme.changed", version = _themeVersion, variables = _themeVariables,
        }, MessageJson));
        if (_ackedThemeVersion < _themeVersion && _watchedThemeVersion != _themeVersion)
        {
            _watchedThemeVersion = _themeVersion;
            _ = WatchThemeAckAsync(_themeVersion);
        }
    }

    private async Task WatchThemeAckAsync(long version)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (_disposed || State != WebSurfaceState.Ready || _themeVersion != version
            || _ackedThemeVersion >= version) return;
        ThemeAckTimedOut = true;
        _diagnostics?.Invoke("WEB_THEME_ACK_TIMEOUT");
        // One replay is bounded; a silent page never freezes the WPF shell.
        _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "theme.changed", version = _themeVersion, variables = _themeVariables,
        }, MessageJson));
    }

    private void Fail(string code)
    {
        State = WebSurfaceState.Failed;
        ErrorCode = code;
        _webView.Visibility = Visibility.Collapsed;
        _fallback.Text = "页面暂时无法打开。请稍后重试。";
        _fallback.Visibility = Visibility.Visible;
        _diagnostics?.Invoke(code);
    }

    private static JsonSerializerOptions CreateMessageJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
