using System.IO;
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
    public ChatWebSurfaceFactory(IChatWebSessionFactory sessions, string storageRoot, ThemeService? theme = null)
    { _sessions = sessions; _storageRoot = Path.GetFullPath(storageRoot); _theme = theme; }

    public WebView2SurfaceHost CreateView(IChatWebHostActions actions)
    {
        var session = _sessions.Create(actions);
        try
        {
            var host = new WebView2SurfaceHost(new WebSurfacePolicy("https://chat.fgopet.invalid/index.html",
                ["chat.get", "chat.draft", "chat.send", "chat.stop", "chat.new", "chat.history",
                 "chat.history.open", "chat.history.requestDelete", "chat.history.cancelDelete",
                 "chat.history.confirmDelete", "chat.retryCapability", "chat.host"]),
                Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "chat"),
                Path.Combine(_storageRoot, "WebView2", "chat"), session.HandleCommandAsync);
            void Publish(ChatWebSnapshot snapshot) => host.PostEvent(new { type = "chat.snapshot", payload = snapshot });
            session.Changed += Publish;
            host.Ready += (_, _) => Publish(session.ReadSnapshot());
            host.Disposed += (_, _) => { session.Changed -= Publish; session.Dispose(); };
            if (_theme is not null)
            {
                var version = 1L;
                EventHandler changed = (_, _) => host.SetThemeVersion(Interlocked.Increment(ref version), _theme.CaptureWebPalette());
                _theme.ThemeChanged += changed;
                host.Disposed += (_, _) => _theme.ThemeChanged -= changed;
                host.SetThemeVersion(version, _theme.CaptureWebPalette());
            }
            return host;
        }
        catch { session.Dispose(); throw; }
    }
}
