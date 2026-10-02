using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Portraits;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FgoPet.App.Portraits.Live2D;

/// <summary>App-owned browser surface above the always-available static portrait.</summary>
internal sealed class Live2DPortraitView : Grid, IDisposable
{
    private const string Host = "fgo-pet-live2d.local";
    private readonly PortraitView _staticView = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    private readonly string _profileRoot;
    private WebView2CompositionControl? _browser;
    private Live2DSession? _session;
    private bool _modelReady;
    private bool _started;
    private bool _failed;
    private bool _disposed;

    public Live2DPortraitView(string profileRoot)
    {
        _profileRoot = profileRoot;
        Children.Add(_staticView);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event Action<Live2DHitMask>? Ready;
    public event Action? Failed;
    public Live2DHitMask? HitMask { get; private set; }

    public void PlayTap()
    {
        if (HitMask is not null) _browser?.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"live2d.tap\"}");
    }

    public void Present(IPortraitFrame staticFrame, Live2DSession? session, bool showLive)
    {
        staticFrame.Present(_staticView);
        _staticView.Width = staticFrame.Geometry.LogicalSize.Width;
        _staticView.Height = staticFrame.Geometry.LogicalSize.Height;
        if (!ReferenceEquals(_session, session))
        {
            CloseBrowser();
            _session = session;
            _failed = false;
            EnsureBrowser();
        }
        _staticView.Visibility = showLive && HitMask is not null ? Visibility.Hidden : Visibility.Visible;
        if (_browser is not null)
        {
            _browser.Visibility = showLive && HitMask is not null ? Visibility.Visible : Visibility.Hidden;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        CloseBrowser();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        EnsureBrowser();
        if (_session is not null && !_started) _ = StartAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => CloseBrowser();

    private void EnsureBrowser()
    {
        if (_session is null || _browser is not null || _disposed || _failed) return;
        _browser = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            Visibility = Visibility.Hidden,
        };
        Children.Add(_browser);
        if (IsLoaded) _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (_started || _browser is null || _session is null || _disposed) return;
        _started = true;
        var browser = _browser;
        var session = _session;
        try
        {
            Directory.CreateDirectory(_profileRoot);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profileRoot);
            if (!ReferenceEquals(browser, _browser) || !ReferenceEquals(session, _session)) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (!ReferenceEquals(browser, _browser) || !ReferenceEquals(session, _session)) return;
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping(Host, session.Root, CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!IsLocal(args.Request.Uri))
                {
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain");
                }
            };
            core.NavigationStarting += (_, args) => args.Cancel = !IsLocal(args.Uri);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += (_, _) => Fail(browser);
            browser.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess) Fail(browser);
            };
            browser.Source = new Uri($"https://{Host}/index.html");
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (ReferenceEquals(browser, _browser) && HitMask is null) Fail(browser);
        }
        catch (Exception)
        {
            Fail(browser);
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_browser is null || !ReferenceEquals(sender, _browser.CoreWebView2) || !IsLocal(args.Source)) return;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "live2d.ready")
            {
                _modelReady = true;
            }
            else if (type.GetString() == "live2d.hitmask" && _modelReady)
            {
                var mask = Live2DHitMask.Parse(
                    root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(),
                    root.GetProperty("data").GetString());
                if (mask is null) return;
                var first = HitMask is null;
                HitMask = mask;
                if (first) Ready?.Invoke(mask);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Ignore malformed messages; they cannot change renderer state.
        }
    }

    private static bool IsLocal(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && parsed.Host == Host;

    private void Fail(WebView2CompositionControl browser)
    {
        if (!ReferenceEquals(browser, _browser) || _failed) return;
        _failed = true;
        CloseBrowser();
        Failed?.Invoke();
    }

    private void CloseBrowser()
    {
        _started = false;
        _modelReady = false;
        HitMask = null;
        if (_browser is not { } browser) return;
        _browser = null;
        Children.Remove(browser);
        try
        {
            browser.Dispose();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A crashed browser process must not surface through the portrait; Static stays visible.
        }
        _staticView.Visibility = Visibility.Visible;
    }
}
