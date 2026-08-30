using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.App.Lifetime;
using FgoPet.App.Main;
using FgoPet.App.Portraits;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Tray;
using FgoPet.App.Windowing;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;

namespace FgoPet.App.Bootstrap;

public delegate Task PortraitActivation(PortraitSelection selection, CancellationToken cancellationToken);

/// <summary>Owns the production WPF windows, tray callbacks, and portrait context menu.</summary>
public sealed class DesktopAppUi : IDesktopAppUi, IDisposable
{
    private readonly TrayService _tray;
    private readonly ServantLibraryViewModel _libraryViewModel;
    private readonly SettingsWindow _settings;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly PortraitWindow _portrait;
    private readonly PortraitWindowCoordinator _coordinator;
    private readonly IAppLifetime _lifetime;
    private readonly AppPaths _paths;
    private readonly PortraitController _controller;
    private readonly ConversationViewModel? _conversation;
    private readonly PortraitActivation _activatePortrait;
    private readonly IAppSettingsStore? _appSettings;
    private readonly ContextMenu _portraitMenu;
    private bool _initialized;
    private Task? _portraitRecovery;

    public DesktopAppUi(
        TrayService tray,
        ServantLibraryViewModel libraryViewModel,
        SettingsWindow settings,
        SettingsViewModel settingsViewModel,
        PortraitWindow portrait,
        PortraitWindowCoordinator coordinator,
        IAppLifetime lifetime,
        AppPaths paths,
        PortraitController controller,
        ConversationViewModel? conversation = null,
        PortraitActivation? portraitActivation = null,
        IAppSettingsStore? appSettings = null)
    {
        _tray = tray;
        _libraryViewModel = libraryViewModel;
        _settings = settings;
        _settingsViewModel = settingsViewModel;
        _portrait = portrait;
        _coordinator = coordinator;
        _lifetime = lifetime;
        _paths = paths;
        _controller = controller;
        _conversation = conversation;
        _activatePortrait = portraitActivation
            ?? (controller is null ? ((_, _) => Task.CompletedTask) : controller.ActivateAsync);
        _appSettings = appSettings;
        _portraitMenu = CreatePortraitMenu();
        _tray.ShowHideRequested += OnTrayShowHideRequested;
        _tray.RestoreRequested += OnTrayRestoreRequested;
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.OpenPackFolderRequested += (_, _) => OpenPackagesRoot();
        _tray.ExitRequested += (_, _) => Exit();
        if (_conversation is not null)
        {
            _conversation.SettingsRequested += section => ShowSettings(section);
        }
    }

    public void InitializeTray()
    {
        if (_initialized) return;
        _initialized = true;
        _lifetime.AttachPetWindow(_portrait);
        _portrait.ContextMenu = _portraitMenu;
        _controller.StateChanged += OnPortraitStateChanged;
    }

    public void ShowLibrary(string? offeredPackPath = null)
    {
        if (!string.IsNullOrWhiteSpace(offeredPackPath))
        {
            _libraryViewModel.PackFilePath = offeredPackPath;
        }
        ShowSettings(SettingsSection.RolePackages);
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

    public ContextMenu PortraitMenu => _portraitMenu;

    public void ShowPortrait()
    {
        _lifetime.AttachPetWindow(_portrait);
        _coordinator.InitializePlacement();
        _portrait.Show();
    }

    public void Dispose()
    {
        _tray.ShowHideRequested -= OnTrayShowHideRequested;
        _tray.RestoreRequested -= OnTrayRestoreRequested;
        _controller.StateChanged -= OnPortraitStateChanged;
    }

    private ContextMenu CreatePortraitMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("设置", (_, _) => ShowSettings()));
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

    private void OnTrayShowHideRequested(object? sender, EventArgs e)
    {
        if (_portrait.Dispatcher.CheckAccess())
        {
            ToggleOwnedPortrait();
            return;
        }

        _portrait.Dispatcher.BeginInvoke(ToggleOwnedPortrait, DispatcherPriority.Send);
    }

    private void ToggleOwnedPortrait()
    {
        if (_portrait.IsVisible)
        {
            _portrait.Hide();
            return;
        }

        if (_controller is null || _controller.CurrentState is not null || _appSettings is null)
        {
            ShowPortrait();
            _portrait.Activate();
            return;
        }

        if (_portraitRecovery is null || _portraitRecovery.IsCompleted)
        {
            _portraitRecovery = RecoverAndShowPortraitAsync();
        }
    }

    private async Task RecoverAndShowPortraitAsync()
    {
        var selection = _appSettings?.Load().Selection;
        if (selection is null)
        {
            ShowSettings(SettingsSection.RolePackages);
            return;
        }

        try
        {
            await _activatePortrait(selection, CancellationToken.None);
            ShowPortrait();
            _portrait.Activate();
        }
        catch (Exception error)
        {
            ShowSettings(SettingsSection.RolePackages);
            MessageBox.Show(
                $"人物加载失败：{error.Message}",
                "FGO Pet",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Exit()
    {
        _lifetime.RequestNormalExit();
    }
}
