using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;

namespace FgoPet.App.Servants;

/// <summary>
/// Drives the independent servant library: browsing, appearance selection, install,
/// activation, uninstall, rescan, diagnostics, and opening the pack folder.
/// </summary>
public sealed partial class ServantLibraryViewModel : ObservableObject
{
    private readonly IArtPackageRepository _repository;
    private readonly IPackInstaller _installer;
    private readonly IPortraitController _controller;
    private readonly IAppSettingsStore _settings;
    private readonly ServantPreferenceService _preferences;
    private readonly Action<string> _openFolder;

    [ObservableProperty]
    private IReadOnlyList<ServantCardViewModel> _servants = Array.Empty<ServantCardViewModel>();

    [ObservableProperty]
    private ServantCardViewModel? _selectedServant;

    [ObservableProperty]
    private ServantAppearanceItemViewModel? _currentAppearance;

    [ObservableProperty]
    private PackageDiagnosticViewModel? _diagnostic;

    [ObservableProperty]
    private bool _isDiagnosticVisible;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _packFilePath = string.Empty;

    [ObservableProperty]
    private string _scanStatus = "尚未扫描角色包。";

    [ObservableProperty]
    private bool _usePackageDefaultAddress = true;

    [ObservableProperty]
    private bool _useCustomAddress;

    [ObservableProperty]
    private string _customAddress = string.Empty;

    [ObservableProperty]
    private string _addressStatus = string.Empty;

    public ServantLibraryViewModel(
        IArtPackageRepository repository,
        IPackInstaller installer,
        IPortraitController controller,
        IAppSettingsStore settings,
        Action<string>? openFolder = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _preferences = new ServantPreferenceService(_settings);
        _openFolder = openFolder ?? (path => Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }));

        RescanCommand = new AsyncRelayCommand(LoadAsync);
        InstallCommand = new AsyncRelayCommand(() => InstallAsync(PackFilePath));
        ActivateCommand = new AsyncRelayCommand(ActivateAsync, () => CurrentAppearance is not null);
        UninstallCommand = new AsyncRelayCommand(
            UninstallAsync,
            () => SelectedServant is { IsEmbedded: false } && CurrentAppearance is not null);
        OpenPackFolderCommand = new AsyncRelayCommand(
            OpenPackFolderAsync,
            () => SelectedServant is not null);
        SaveAddressCommand = new AsyncRelayCommand(SaveAddressAsync, () => SelectedServant is not null);
    }

    public IRelayCommand RescanCommand { get; }

    public IAsyncRelayCommand InstallCommand { get; }

    public IAsyncRelayCommand ActivateCommand { get; }

    public IAsyncRelayCommand UninstallCommand { get; }

    public IAsyncRelayCommand OpenPackFolderCommand { get; }

    public IAsyncRelayCommand SaveAddressCommand { get; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Diagnostic = null;
        try
        {
            var servants = await _repository.ListServantsAsync(CancellationToken.None);
            var cards = servants
                .Select(servant => new ServantCardViewModel(
                    servant.PackageId,
                    servant.ServantId,
                    servant.DisplayName,
                    SourceBadge(servant),
                    isEmbedded: string.Equals(servant.Publisher, "embedded", StringComparison.OrdinalIgnoreCase),
                    servant.Appearances.Select(appearance =>
                        new ServantAppearanceItemViewModel(appearance.AppearanceId, appearance.PackageVersion, appearance.PreviewPath)).ToList()))
                .ToList();
            Servants = cards;
            ScanStatus = $"已发现 {cards.Count} 个从者";
            if (cards.Count == 0 && _repository is IPackScanDiagnostics diagnostics && diagnostics.LastScanIssues.Count > 0)
            {
                ScanStatus += $" · {diagnostics.LastScanIssues[0]}";
            }

            if (SelectedServant is not null)
            {
                var refreshed = cards.FirstOrDefault(card => card.PackageId == SelectedServant.PackageId)
                    ?? cards.FirstOrDefault();
                SelectedServant = refreshed;
            }
            else
            {
                SelectedServant = cards.FirstOrDefault();
            }

            CurrentAppearance = SelectedServant?.SelectedAppearance;
            LoadAddressPreference(SelectedServant?.ServantId);
        }
        catch (Exception error)
        {
            Servants = Array.Empty<ServantCardViewModel>();
            SelectedServant = null;
            CurrentAppearance = null;
            LoadAddressPreference(null);
            ScanStatus = $"扫描失败：{error.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InstallAsync(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        IsBusy = true;
        Diagnostic = null;
        try
        {
            var result = await _installer.InstallAsync(archivePath, CancellationToken.None);
            if (!result.Installed)
            {
                Diagnostic = new PackageDiagnosticViewModel(result.Failure ?? new PackFailure(PackErrorCode.PackageArchiveInvalid, "安装失败。"));
                return;
            }

            // Installing a pack never auto-activates it; the catalog is refreshed so the
            // user can expressly open it. The foreground pet is unchanged.
            await LoadAsync();
        }
        catch (PackFailureException error)
        {
            Diagnostic = new PackageDiagnosticViewModel(error.Failure);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ActivateAsync()
    {
        if (SelectedServant is null || CurrentAppearance is null)
        {
            return;
        }

        // Keep the current selection in the UI even if activation fails.
        var selection = new PortraitSelection(
            SelectedServant.PackageId,
            CurrentAppearance.AppearanceId,
            CurrentAppearance.PackageVersion);
        Diagnostic = null;

        try
        {
            await _controller.ActivateAsync(selection, CancellationToken.None);
        }
        catch (PackFailureException error)
        {
            Diagnostic = new PackageDiagnosticViewModel(error.Failure);
            return;
        }

        var settings = _settings.Load();
        _settings.Save(new AppSettings(
            selection,
            settings.Scale,
            settings.Topmost,
            settings.AutoCollapseExpandedPanel));
    }

    public async Task UninstallAsync()
    {
        if (SelectedServant is null or { IsEmbedded: true } || CurrentAppearance is null)
        {
            return;
        }

        var removed = await _repository.RemoveAsync(SelectedServant.PackageId, CurrentAppearance.PackageVersion, CancellationToken.None);
        if (!removed)
        {
            Diagnostic = new PackageDiagnosticViewModel(new PackFailure(
                PackErrorCode.PackageArchiveInvalid,
                "当前角色包需先切换到其他有效包后才能卸载。"));
            return;
        }

        Diagnostic = null;
        await LoadAsync();
    }

    public async Task<string?> OpenPackFolderAsync()
    {
        var root = await ResolveSelectedPackRootAsync();
        if (!string.IsNullOrWhiteSpace(root))
        {
            _openFolder(root);
        }

        return root;
    }

    private async Task<string?> ResolveSelectedPackRootAsync()
    {
        if (SelectedServant is null)
        {
            return null;
        }

        var catalog = await _repository.ScanAsync(CancellationToken.None);
        return catalog.Packs
            .Where(pack => pack.PackageId == SelectedServant.PackageId)
            .OrderByDescending(pack => pack.Version)
            .FirstOrDefault()?.PackRoot;
    }

    partial void OnDiagnosticChanged(PackageDiagnosticViewModel? value) => IsDiagnosticVisible = value is not null;

    partial void OnSelectedServantChanged(ServantCardViewModel? value)
    {
        CurrentAppearance = value?.SelectedAppearance;
        LoadAddressPreference(value?.ServantId);
        UninstallCommand.NotifyCanExecuteChanged();
        OpenPackFolderCommand.NotifyCanExecuteChanged();
        SaveAddressCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentAppearanceChanged(ServantAppearanceItemViewModel? value)
    {
        ActivateCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private static string SourceBadge(InstalledServant servant) =>
        string.Equals(servant.Publisher, "embedded", StringComparison.OrdinalIgnoreCase)
            ? "内置"
            : "来源未验证";

    private void LoadAddressPreference(string? servantId)
    {
        if (string.IsNullOrWhiteSpace(servantId))
        {
            UsePackageDefaultAddress = true;
            UseCustomAddress = false;
            CustomAddress = string.Empty;
            AddressStatus = string.Empty;
            return;
        }

        var preference = _preferences.LoadAsync(servantId).GetAwaiter().GetResult();
        UsePackageDefaultAddress = preference.AddressMode == FgoPet.Core.Settings.AddressMode.PackageDefault;
        UseCustomAddress = preference.AddressMode == FgoPet.Core.Settings.AddressMode.UserDefined;
        CustomAddress = preference.AddressText ?? string.Empty;
        AddressStatus = string.Empty;
    }

    private async Task SaveAddressAsync()
    {
        if (SelectedServant is null) return;
        if (UseCustomAddress && string.IsNullOrWhiteSpace(CustomAddress))
        {
            AddressStatus = "请输入自定义称呼。";
            return;
        }

        var preference = UseCustomAddress
            ? new ServantPreference(FgoPet.Core.Settings.AddressMode.UserDefined, CustomAddress)
            : new ServantPreference(FgoPet.Core.Settings.AddressMode.PackageDefault);
        await _preferences.SaveAsync(SelectedServant.ServantId, preference);
        AddressStatus = "称呼已保存。";
    }

    partial void OnUsePackageDefaultAddressChanged(bool value)
    {
        if (value) UseCustomAddress = false;
    }

    partial void OnUseCustomAddressChanged(bool value)
    {
        if (value) UsePackageDefaultAddress = false;
    }
}
