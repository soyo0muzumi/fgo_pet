using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.App.Lifetime;
using FgoPet.App.Main;
using FgoPet.App.Panels;
using FgoPet.UiSdk;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Tray;
using FgoPet.App.Windowing;
using FgoPet.Character.Settings;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Bootstrap;

public delegate Task PortraitActivation(PortraitSelection selection, CancellationToken cancellationToken);

/// <summary>Owns the production WPF windows, tray callbacks, and portrait context menu.</summary>
public sealed class DesktopAppUi : IDesktopAppUi, IDisposable
{
    private readonly TrayService _tray;
    private readonly ServantLibraryViewModel _libraryViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly SettingsWindow? _settingsWindow;
    private readonly PortraitWindow _portrait;
    private readonly PortraitWindowCoordinator _coordinator;
    private readonly IAppLifetime _lifetime;
    private readonly AppPaths _paths;
    private readonly IPortraitSurface _controller;
    private readonly ConversationViewModel? _conversation;
    private readonly DialogueWindow? _dialogueWindow;
    private readonly DialogueWindowViewModel? _dialogue;
    private readonly AttachedPanelViewModel? _attachedPanel;
    private readonly DialogueWindowPlacementCoordinator? _dialoguePlacement;
    private readonly PortraitActivation _activatePortrait;
    private readonly ICharacterSettingsStore? _characterSettings;
    private readonly ContextMenu _portraitMenu;
    private bool _initialized;
    private Task? _portraitRecovery;
    private bool _portraitHiddenByUser;

    /// <summary>Compatibility overload for legacy tests; production composition uses the shared DialogueWindow.</summary>
    public DesktopAppUi(
        TrayService tray,
        ServantLibraryViewModel libraryViewModel,
        SettingsWindow settingsWindow,
        SettingsViewModel settingsViewModel,
        PortraitWindow portrait,
        PortraitWindowCoordinator coordinator,
        IAppLifetime lifetime,
        AppPaths paths,
        IPortraitSurface controller,
        ConversationViewModel? conversation = null,
        PortraitActivation? portraitActivation = null,
        ICharacterSettingsStore? characterSettings = null,
        DialogueWindow? dialogueWindow = null,
        DialogueWindowViewModel? dialogue = null,
        AttachedPanelViewModel? attachedPanel = null,
        DialogueWindowPlacementCoordinator? dialoguePlacement = null)
        : this(
            tray,
            libraryViewModel,
            settingsViewModel,
            portrait,
            coordinator,
            lifetime,
            paths,
            controller,
            conversation,
            portraitActivation,
            characterSettings,
            dialogueWindow,
            dialogue,
            attachedPanel,
            dialoguePlacement)
    {
        _settingsWindow = settingsWindow;
    }
    public DesktopAppUi(
        TrayService tray,
        ServantLibraryViewModel libraryViewModel,
        SettingsViewModel settingsViewModel,
        PortraitWindow portrait,
        PortraitWindowCoordinator coordinator,
        IAppLifetime lifetime,
        AppPaths paths,
        IPortraitSurface controller,
        ConversationViewModel? conversation = null,
        PortraitActivation? portraitActivation = null,
        ICharacterSettingsStore? characterSettings = null,
        DialogueWindow? dialogueWindow = null,
        DialogueWindowViewModel? dialogue = null,
        AttachedPanelViewModel? attachedPanel = null,
        DialogueWindowPlacementCoordinator? dialoguePlacement = null)
    {
        _tray = tray;
        _libraryViewModel = libraryViewModel;
        _settingsViewModel = settingsViewModel;
        _portrait = portrait;
        _coordinator = coordinator;
        _lifetime = lifetime;
        _paths = paths;
        _controller = controller;
        _conversation = conversation;
        _dialogueWindow = dialogueWindow;
        _dialogue = dialogue;
        _attachedPanel = attachedPanel;
        _dialoguePlacement = dialoguePlacement;
        if (_attachedPanel is not null)
        {
            _attachedPanel.SettingsRequested += OnAttachedPanelSettingsRequested;
            _attachedPanel.ExitRequested += OnAttachedPanelExitRequested;
        }
        _activatePortrait = portraitActivation
            ?? (controller is null ? ((_, _) => Task.CompletedTask) : controller.ActivateAsync);
        _characterSettings = characterSettings;
        _portraitMenu = CreatePortraitMenu();
        if (portrait is not null) portrait.ChatRequested += ShowDialogueWindow;
        _tray.ChatRequested += OnTrayChatRequested;
        _tray.ShowHideRequested += OnTrayShowHideRequested;
        _tray.RestoreRequested += OnTrayRestoreRequested;
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.OpenPackFolderRequested += (_, _) => OpenPackagesRoot();
        _tray.ExitRequested += (_, _) => Exit();
        if (_conversation is not null)
        {
            _conversation.SettingsRequested += section => ShowSettings(section);
        }
        if (_dialogueWindow is not null && _dialogue is not null)
        {
            _dialogueWindow.PresentationRequestedHandler = ShowDialogueWindow;
            if (dialoguePlacement is not null && coordinator is not null)
                _dialogueWindow.LayoutRequestedHandler = OnGeometryChanged;
            _dialogue.SettingsRequested += OnDialogueSettingsRequested;
        }
    }

    public void InitializeTray()
    {
        if (_initialized) return;
        _initialized = true;
        _lifetime.AttachPetWindow(_portrait);
        _coordinator.InitializeChatShortcut();
        _portrait.ContextMenu = _portraitMenu;
        _controller.StateChanged += OnPortraitStateChanged;
        _portrait.ManagementHitTest = IsManagementHit;
        _coordinator.GeometryChanged += OnGeometryChanged;
        if (_dialogueWindow is not null)
        {
            _dialogueWindow.IsVisibleChanged += OnManagementVisibilityChanged;
            _dialogueWindow.StateChanged += OnManagementStateChanged;
        }
        if (_settingsWindow is not null)
        {
            _settingsWindow.IsVisibleChanged += OnManagementVisibilityChanged;
            _settingsWindow.StateChanged += OnManagementStateChanged;
        }
    }

    public void ShowLibrary(string? offeredPackPath = null)
    {
        // Packless startup is a first-run flow in the existing DialogueWindow.
        // Keeping this route on the ordinary window guarantees a taskbar entry and
        // shares the same navigation/context contract as normal conversation.
        if (!string.IsNullOrWhiteSpace(offeredPackPath))
        {
            _libraryViewModel.PackFilePath = offeredPackPath;
        }
        ShowSettings(SettingsSection.RolePackages);
    }

    public void ShowDialogueWindow()
    {
        if (_dialogueWindow is null)
        {
            return;
        }

        if (_conversation is not null
            && _attachedPanel is not null
            && !string.IsNullOrWhiteSpace(_attachedPanel.ActiveServantId)
            && !string.Equals(_conversation.ActiveServantId, _attachedPanel.ActiveServantId, StringComparison.Ordinal))
        {
            _conversation.SetActiveServant(_attachedPanel.ActiveServantId);
        }

        if (_dialogueWindow.IsVisible && _dialogueWindow.WindowState != WindowState.Minimized)
        {
            _dialogueWindow.Activate();
            return;
        }

        // Independent native windows keep Chat restorable when the character is hidden.
        if (_coordinator is not null) _dialoguePlacement?.ApplyOnOpen(_dialogueWindow, _coordinator.PortraitDeviceBounds);
        _dialogueWindow.RestorePresentation();
        UpdateManagementPriority();
    }

    public void ShowSettings(SettingsSection? section = null)
    {
        _settingsViewModel.Select(section ?? SettingsSection.Personalization);
        _settingsWindow?.NavigateToSelectedPage();

        if (_settingsWindow is null)
        {
            return;
        }

        if (_settingsWindow.IsVisible)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            if (_coordinator is not null) _dialoguePlacement?.ApplyOnOpen(_settingsWindow, _coordinator.PortraitDeviceBounds);
            UpdateManagementPriority();
            _settingsWindow.Activate();
            return;
        }

        if (_coordinator is not null) _dialoguePlacement?.ApplyOnOpen(_settingsWindow, _coordinator.PortraitDeviceBounds);
        _settingsWindow.Show();
        _settingsWindow.Activate();
        UpdateManagementPriority();
    }

    public ContextMenu PortraitMenu => _portraitMenu;

    public void ShowFirstStartChat()
    {
        if (_dialogue is null)
        {
            return;
        }

        _dialogue.NavigateTo(MainNavigationTarget.Companion);
        ShowDialogueWindow();

        if (_dialogue.Conversation.IsConfigurationRequired)
        {
            ShowSettings(SettingsSection.ModelConnection);
        }
    }
    public void ShowPortrait()
    {
        _lifetime.AttachPetWindow(_portrait);
        _coordinator.InitializePlacement();
        _portrait.Show();
    }

    public void Dispose()
    {
        if (_portrait is not null) _portrait.ChatRequested -= ShowDialogueWindow;
        _tray.ChatRequested -= OnTrayChatRequested;
        if (_portrait is not null) _portrait.ManagementHitTest = null;
        if (_coordinator is not null) _coordinator.GeometryChanged -= OnGeometryChanged;
        if (_dialogueWindow is not null)
        {
            _dialogueWindow.IsVisibleChanged -= OnManagementVisibilityChanged;
            _dialogueWindow.StateChanged -= OnManagementStateChanged;
        }
        if (_settingsWindow is not null)
        {
            _settingsWindow.IsVisibleChanged -= OnManagementVisibilityChanged;
            _settingsWindow.StateChanged -= OnManagementStateChanged;
        }
        _tray.ShowHideRequested -= OnTrayShowHideRequested;
        _tray.RestoreRequested -= OnTrayRestoreRequested;
        if (_controller is not null) _controller.StateChanged -= OnPortraitStateChanged;
        if (_dialogue is not null)
        {
            _dialogue.SettingsRequested -= OnDialogueSettingsRequested;
            if (_dialogueWindow is not null)
            {
                _dialogueWindow.PresentationRequestedHandler = null;
                _dialogueWindow.LayoutRequestedHandler = null;
            }
        }
        if (_attachedPanel is not null)
        {
            _attachedPanel.SettingsRequested -= OnAttachedPanelSettingsRequested;
            _attachedPanel.ExitRequested -= OnAttachedPanelExitRequested;
        }
    }

    private void OnDialogueSettingsRequested(SettingsSection section) => ShowSettings(section);

    private void OnAttachedPanelSettingsRequested(SettingsSection section) => ShowSettings(section);

    private void OnAttachedPanelExitRequested() => Exit();

    private ContextMenu CreatePortraitMenu()
    {
        var menu = new ContextMenu
        {
            MaxWidth = 268, MaxHeight = 430,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 250, 248)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 31, 27)),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
        };
        menu.Items.Add(Item(MarginStrings.Get("entry.chat"), (_, _) => ShowDialogueWindow()));
        menu.Items.Add(Item(MarginStrings.Get("entry.settings"), (_, _) => ShowSettings()));
        menu.Items.Add(Item(MarginStrings.Get("entry.hide"), (_, _) => ToggleOwnedPortrait()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(MarginStrings.Get("entry.exit"), (_, _) => Exit()));
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
            if (!_portrait.IsVisible && !_portraitHiddenByUser)
            {
                ShowPortrait();
            }
        });

    private void OnTrayRestoreRequested(object? sender, EventArgs e)
    {
        if (!_portrait.IsVisible) ToggleOwnedPortrait();
        ShowDialogueWindow();
    }

    private void OnTrayChatRequested(object? sender, EventArgs e) => ShowDialogueWindow();

    private void UpdateManagementPriority()
    {
        if (_portrait is null) return;
        if (_dialogueWindow is not null) _dialogueWindow.Topmost = _portrait.Topmost;
        if (_settingsWindow is not null) _settingsWindow.Topmost = _portrait.Topmost;
    }

    private void OnManagementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateManagementPriority();
    private void OnManagementStateChanged(object? sender, EventArgs args) => UpdateManagementPriority();
    private bool IsManagementHit(Point screenPoint) => Contains(_dialogueWindow, screenPoint) || Contains(_settingsWindow, screenPoint);
    private static bool Contains(Window? window, Point screenPoint)
    {
        return TransientSurfacePlacement.ContainsScreenPoint(window, screenPoint);
    }
    private void OnGeometryChanged()
    {
        if (_dialogueWindow?.IsVisible == true && _dialogueWindow.WindowState != WindowState.Minimized)
            _dialoguePlacement?.ApplyOnOpen(_dialogueWindow, _coordinator.PortraitDeviceBounds);
        if (_settingsWindow?.IsVisible == true && _settingsWindow.WindowState != WindowState.Minimized)
            _dialoguePlacement?.ApplyOnOpen(_settingsWindow, _coordinator.PortraitDeviceBounds);
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
            _portraitHiddenByUser = true;
            _portrait.Hide();
            return;
        }

        _portraitHiddenByUser = false;
        if (_controller is null || _controller.CurrentFrame is not null || _characterSettings is null)
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
        var selection = _characterSettings?.Load().Selection;
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
