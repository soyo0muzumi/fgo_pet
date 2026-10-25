using System.Drawing;
using System.Windows.Forms;
using FgoPet.UiSdk;

namespace FgoPet.App.Tray;

/// <summary>
/// System tray icon with a fixed menu: show/hide, settings, open pack folder, and
/// exit. Owns a NotifyIcon for as long as the process runs.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _applicationIcon;
    private bool _disposed;

    public TrayService()
    {
        _applicationIcon = LoadApplicationIcon();
        _icon = new NotifyIcon
        {
            Text = "FGO Pet",
            Icon = _applicationIcon,
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(MarginStrings.Get("entry.chat"), null, (_, _) => ChatRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(MarginStrings.Get("entry.restore"), null, (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(MarginStrings.Get("entry.settings"), null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(MarginStrings.Get("entry.packFolder"), null, (_, _) => OpenPackFolderRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(MarginStrings.Get("entry.exit"), null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => HandleDoubleClick();
    }

    internal System.Windows.Forms.ContextMenuStrip Menu => (System.Windows.Forms.ContextMenuStrip)_icon.ContextMenuStrip!;

    public event EventHandler? ShowHideRequested;
    public event EventHandler? ChatRequested;

    public event EventHandler? RestoreRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? OpenPackFolderRequested;

    public event EventHandler? ExitRequested;

    internal void HandleDoubleClick() => RestoreRequested?.Invoke(this, EventArgs.Empty);

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri(
            "/FgoPet.DesktopShell;component/Assets/AppIcon.ico",
            UriKind.Relative)) ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }

    /// <summary>Submit a local Windows notification. This is not a receipt that a user saw it.</summary>
    public bool ShowNotification(string title, string message)
    {
        if (_disposed || !_icon.Visible) return false;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 63 || string.IsNullOrWhiteSpace(message) || message.Length > 255)
            throw new ArgumentException("Invalid notification content.");
        _icon.ShowBalloonTip(10_000, title, message, ToolTipIcon.Info);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _applicationIcon.Dispose();
    }
}
