using System.Drawing;
using System.Windows.Forms;

namespace FgoPet.App.Tray;

/// <summary>
/// System tray icon with a fixed menu: show/hide, servant library &amp; settings,
/// open pack folder, and exit. Owns a NotifyIcon for as long as the process runs.
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
        menu.Items.Add("显示/隐藏", null, (_, _) => ShowHideRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("从者库与设置", null, (_, _) => LibraryRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("打开角色包目录", null, (_, _) => OpenPackFolderRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
    }

    public event EventHandler? ShowHideRequested;

    public event EventHandler? LibraryRequested;

    public event EventHandler? OpenPackFolderRequested;

    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}