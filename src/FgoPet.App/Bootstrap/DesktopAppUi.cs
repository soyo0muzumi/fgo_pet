using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using FgoPet.App.Lifetime;
using FgoPet.App.Main;
using FgoPet.App.Portraits;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Tray;
using FgoPet.App.Windowing;

namespace FgoPet.App.Bootstrap;

/// <summary>Owns the production WPF windows, tray callbacks, and portrait context menu.</summary>
public sealed class DesktopAppUi : IDesktopAppUi, IDisposable
{
    private readonly TrayService _tray;
    private readonly ServantLibraryWindow _library;
    private readonly ServantLibraryViewModel _libraryViewModel;
    private readonly ModelConnectionWindow _modelConnection;
    private readonly SettingsWindow _settings;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly PortraitWindow _portrait;
    private readonly PortraitWindowCoordinator _coordinator;
    private readonly IAppLifetime _lifetime;
    private readonly AppPaths _paths;
    private readonly PortraitController _controller;
    private bool _initialized;
    private bool _allowLibraryClose;

    public DesktopAppUi(
        TrayService tray,
        ServantLibraryWindow library,
        ServantLibraryViewModel libraryViewModel,
        ModelConnectionWindow modelConnection,
        SettingsWindow settings,
        SettingsViewModel settingsViewModel,
        PortraitWindow portrait,
        PortraitWindowCoordinator coordinator,
        IAppLifetime lifetime,
        AppPaths paths,
        PortraitController controller)
    {
        _tray = tray;
        _library = library;
        _libraryViewModel = libraryViewModel;
        _modelConnection = modelConnection;
        _settings = settings;
        _settingsViewModel = settingsViewModel;
        _portrait = portrait;
        _coordinator = coordinator;
        _lifetime = lifetime;
        _paths = paths;
        _controller = controller;
    }

    public void InitializeTray()
    {
        if (_initialized) return;
        _initialized = true;
        _tray.ShowHideRequested += (_, _) => _lifetime.ShowOrHidePet();
        _tray.RestoreRequested += OnTrayRestoreRequested;
        _tray.LibraryRequested += (_, _) => ShowLibrary();
        _tray.ModelConnectionRequested += (_, _) => ShowModelConnection();
        _tray.OpenPackFolderRequested += (_, _) => OpenPackagesRoot();
        _tray.ExitRequested += (_, _) => Exit();
        _portrait.ContextMenu = CreatePortraitMenu();
        _controller.StateChanged += OnPortraitStateChanged;
        _library.Closing += (_, e) =>
        {
            if (_allowLibraryClose) return;
            e.Cancel = true;
            _library.Hide();
        };
    }

    public void ShowLibrary(string? offeredPackPath = null)
    {
        if (!string.IsNullOrWhiteSpace(offeredPackPath))
        {
            _libraryViewModel.PackFilePath = offeredPackPath;
        }
        _library.Show();
        _library.Activate();
    }

    public void ShowModelConnection()
    {
        _modelConnection.Show();
        _modelConnection.Activate();
    }

    public void ShowSettings(SettingsSection? section = null)
    {
        if (section is not null)
        {
            _settingsViewModel.Select(section.Value);
        }

        _settings.Show();
        _settings.Activate();
    }

    public void ShowPortrait()
    {
        _lifetime.AttachPetWindow(_portrait);
        _coordinator.RestorePlacement();
        _portrait.Show();
    }

    public void Dispose()
    {
        _tray.RestoreRequested -= OnTrayRestoreRequested;
        _controller.StateChanged -= OnPortraitStateChanged;
        _allowLibraryClose = true;
    }

    private ContextMenu CreatePortraitMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("从者库与设置", (_, _) => ShowLibrary()));
        menu.Items.Add(Item("模型连接", (_, _) => ShowModelConnection()));
        menu.Items.Add(Item("隐藏", (_, _) => _lifetime.ShowOrHidePet()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出", (_, _) => Exit()));
        return menu;
    }

    private static MenuItem Item(string title, System.Windows.RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = title };
        item.Click += handler;
        return item;
    }

    private void OpenPackagesRoot()
    {
        Directory.CreateDirectory(_paths.PackagesRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", _paths.PackagesRoot) { UseShellExecute = true });
    }

    private void OnPortraitStateChanged(object? sender, EventArgs e) =>
        _portrait.Dispatcher.BeginInvoke(() =>
        {
            if (!_portrait.IsVisible)
            {
                ShowPortrait();
            }
        });

    private void OnTrayRestoreRequested(object? sender, EventArgs e)
    {
        if (!_lifetime.IsPetVisible)
        {
            _lifetime.ShowOrHidePet();
        }
    }

    private void Exit()
    {
        _allowLibraryClose = true;
        _lifetime.RequestNormalExit();
    }
}
