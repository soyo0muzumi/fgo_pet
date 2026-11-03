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
    // Temporary acceptance diagnostics; no browser content or local paths are recorded. Remove before release.
    private static readonly System.Diagnostics.ActivitySource OperationTiming = new("FgoPet.TestTiming.WebSurface");
    private static readonly JsonSerializerOptions MessageJson = CreateMessageJson();
    private readonly WebSurfacePolicy _policy;
    private readonly string _assetsDirectory;
    private readonly string _profileDirectory;
    private readonly Func<WebSurfaceMessage, CancellationToken, ValueTask<WebSurfaceCommandResult>> _commandHandler;
    private readonly Action<string>? _diagnostics;
    private readonly Action<Uri>? _openExternalLink;
    private readonly Func<Uri, CancellationToken, Task<string?>>? _loadImagePreview;
    private int _imageLoads;
    private readonly WebSurfacePolicy? _externalLinkPolicy;
    private readonly string _externalLinkToken = Guid.NewGuid().ToString("N");
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
        Action<string>? diagnostics = null, Action<Uri>? openExternalLink = null,
        Func<Uri, CancellationToken, Task<string?>>? loadImagePreview = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _assetsDirectory = Path.GetFullPath(assetsDirectory ?? throw new ArgumentNullException(nameof(assetsDirectory)));
        _profileDirectory = Path.GetFullPath(profileDirectory ?? throw new ArgumentNullException(nameof(profileDirectory)));
        _commandHandler = commandHandler ?? throw new ArgumentNullException(nameof(commandHandler));
        _diagnostics = diagnostics;
        _openExternalLink = openExternalLink;
        _loadImagePreview = loadImagePreview;
        if (openExternalLink is not null || loadImagePreview is not null)
            _externalLinkPolicy = new(policy.DocumentUrl, ["web.externalLink", "web.imagePreview"]);
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
            if (_externalLinkPolicy is not null)
                await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                    (() => {
                      const token = TOKEN;
                      document.addEventListener('click', event => {
                        if (!event.isTrusted || !navigator.userActivation.isActive) return;
                        const anchor = event.target instanceof Element ? event.target.closest('a[href]') : null;
                        if (!anchor) return;
                        let url; try { url = new URL(anchor.href); } catch { return; }
                        if (!['http:', 'https:'].includes(url.protocol) || url.host === location.host) return;
                        event.preventDefault();
                        window.chrome.webview.postMessage({type:'web.externalLink', requestId:token, payload:{url:url.href}});
                      }, true);
                    })();
                    """.Replace("TOKEN", JsonSerializer.Serialize(_externalLinkToken), StringComparison.Ordinal));
            if (!_disposed) core.Navigate(_policy.DocumentUrl);
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
        using (OperationTiming.StartActivity("web_surface.cancel_lifetime"))
            _lifetime.Cancel();
        using (OperationTiming.StartActivity("web_surface.detach_handlers"))
        {
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
            DetachCoreHandlers();
        }
        try
        {
            using (OperationTiming.StartActivity("web_surface.dispose_webview"))
                _webView.Dispose();
        }
        catch (Exception)
        {
            // A crashed or already torn-down browser process can make disposal throw; a closing
            // window must never surface that exception to the shell.
        }
        using (OperationTiming.StartActivity("web_surface.dispose_lifetime"))
            _lifetime.Dispose();
        State = WebSurfaceState.Closed;
        using (OperationTiming.StartActivity("web_surface.notify_disposed"))
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
        if (_policy.AllowsNavigation(e.Uri)) return;
        e.Cancel = true;
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The first-party surfaces do not use frames.
        e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private void OpenExternalLink(string url, bool isUserInitiated)
    {
        if (_disposed || State != WebSurfaceState.Ready || _openExternalLink is null
            || _webView.CoreWebView2?.Source != _policy.DocumentUrl
            || !_policy.TryGetExternalLink(url, isUserInitiated, out var external)) return;
        try { _openExternalLink(external!); }
        catch (Exception)
        {
            // No URL, provider content or OS exception text enters diagnostics.
            _diagnostics?.Invoke("WEB_EXTERNAL_LINK_FAILED");
        }
    }
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
        var link = _externalLinkPolicy?.ValidateMessage(e.Source, _webView.CoreWebView2?.Source, e.WebMessageAsJson);
        if (link?.Type == "web.imagePreview")
        {
            if (State == WebSurfaceState.Ready && _loadImagePreview is not null
                && link.Payload.EnumerateObject().Count() == 2
                && link.Payload.TryGetProperty("url", out var imageUrl) && imageUrl.ValueKind == JsonValueKind.String
                && link.Payload.TryGetProperty("imageId", out var imageId) && imageId.ValueKind == JsonValueKind.String
                && imageId.GetString() is { Length: > 0 and <= 64 } id && link.RequestId == id
                && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                && _policy.TryGetExternalLink(imageUrl.GetString(), true, out var uri))
                _ = LoadImagePreviewAsync(uri!, id);
            return;
        }
        if (link?.Type == "web.externalLink")
        {
            if (link.RequestId == _externalLinkToken && link.Payload.EnumerateObject().Count() == 1
                && link.Payload.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                OpenExternalLink(url.GetString()!, true);
            return;
        }
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

    private async Task LoadImagePreviewAsync(Uri url, string imageId)
    {
        if (_imageLoads >= 2)
        {
            PostEvent(new { type = "web.imagePreview.result", imageId, errorCode = "IMAGE_PREVIEW_BUSY" });
            return;
        }
        _imageLoads++;
        try
        {
            var dataUrl = ExternalImagePreview.IsAllowedUrl(url)
                ? await _loadImagePreview!(url, _lifetime.Token) : null;
            if (_disposed || _webView.CoreWebView2?.Source != _policy.DocumentUrl) return;
            if (dataUrl is not { Length: <= 5_592_432 } || !dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) dataUrl = null;
            PostEvent(new { type = "web.imagePreview.result", imageId, dataUrl,
                errorCode = dataUrl is null ? "IMAGE_PREVIEW_UNAVAILABLE" : null });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
            {
                _diagnostics?.Invoke("WEB_IMAGE_PREVIEW_FAILED");
                PostEvent(new { type = "web.imagePreview.result", imageId, errorCode = "IMAGE_PREVIEW_UNAVAILABLE" });
            }
        }
        finally { _imageLoads--; }
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
