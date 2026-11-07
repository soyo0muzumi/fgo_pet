using System.IO;
using System.Diagnostics;
using FgoPet.App.Theming;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;

namespace FgoPet.App.Dialogue;

/// <summary>Owns Web presentation lifetimes, never the shared conversation.</summary>
public sealed class ChatWebSurfaceFactory
{
    private readonly IChatWebSessionFactory _sessions;
    private readonly string _storageRoot;
    private readonly ThemeService? _theme;
    private readonly Action<Uri> _openExternalLink;
    private readonly Func<Uri, CancellationToken, Task<string?>>? _loadImagePreview;
    private readonly Func<string, CancellationToken, Task<string?>> _loadSticker;
    public ChatWebSurfaceFactory(IChatWebSessionFactory sessions, string storageRoot, ThemeService? theme = null,
        Action<Uri>? openExternalLink = null, Func<Uri, CancellationToken, Task<string?>>? loadImagePreview = null,
        Func<string, CancellationToken, Task<string?>>? loadSticker = null)
    {
        _sessions = sessions; _storageRoot = Path.GetFullPath(storageRoot); _theme = theme;
        _openExternalLink = openExternalLink ?? OpenInDefaultBrowser;
        _loadImagePreview = loadImagePreview;
        _loadSticker = loadSticker ?? ChatWebRegistration.CreateLocalStickerLoader(
            Environment.GetEnvironmentVariable("FGO_PET_STICKER_ROOT") ?? Path.Combine(_storageRoot, "expression"),
            Path.Combine(_storageRoot, "StickerLibrary"));
    }

    public WebView2SurfaceHost CreateView(IChatWebHostActions actions)
    {
        var session = _sessions.Create(actions);
        var imagePreview = _loadImagePreview is null ? new ExternalImagePreview() : null;
        try
        {
            var host = new WebView2SurfaceHost(new WebSurfacePolicy("https://chat.fgopet.invalid/index.html",
                ["chat.get", "chat.draft", "chat.image", "chat.image.cancel", "chat.send", "chat.stop", "chat.new", "chat.history",
                 "chat.history.open", "chat.history.requestDelete", "chat.history.cancelDelete",
                 "chat.history.confirmDelete", "chat.retryCapability", "chat.host",
                 "chat.input.answer", "chat.input.cancel", "chat.approval.review", "chat.approval.deny", "chat.approval.cancel"]),
                Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "chat"),
                Path.Combine(_storageRoot, "WebView2", "chat"), session.HandleCommandAsync,
                openExternalLink: _openExternalLink, loadImagePreview: _loadImagePreview ?? imagePreview!.LoadAsync,
                loadLocalImage: _loadSticker);
            void Publish(ChatWebSnapshot snapshot) => host.PostEvent(new { type = "chat.snapshot", payload = snapshot });
            session.Changed += Publish;
            host.Ready += (_, _) => Publish(session.ReadSnapshot());
            host.Disposed += (_, _) => { session.Changed -= Publish; session.Dispose(); imagePreview?.Dispose(); };
            host.SetThemeVersion(1, MarginVisualResources.CaptureWebPalette(_theme));
            if (_theme is not null)
            {
                var version = 1L;
                EventHandler changed = (_, _) => host.SetThemeVersion(Interlocked.Increment(ref version), MarginVisualResources.CaptureWebPalette(_theme));
                _theme.ThemeChanged += changed;
                host.Disposed += (_, _) => _theme.ThemeChanged -= changed;
                host.SetThemeVersion(version, MarginVisualResources.CaptureWebPalette(_theme));
            }
            return host;
        }
        catch { session.Dispose(); imagePreview?.Dispose(); throw; }
    }

    private static void OpenInDefaultBrowser(Uri link)
    {
        using var process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
    }
}
