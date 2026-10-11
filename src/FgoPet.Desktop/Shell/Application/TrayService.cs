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

    public TrayService()
    {
        _icon = new NotifyIcon
        {
            Text = "FGO Pet",
            Icon = SystemIcons.Application,
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
