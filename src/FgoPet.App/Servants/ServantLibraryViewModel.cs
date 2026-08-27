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
        _openFolder = openFolder ?? (path => Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }));
    }

    public IRelayCommand RescanCommand => new AsyncRelayCommand(LoadAsync);

    public IAsyncRelayCommand InstallCommand => new AsyncRelayCommand(() => InstallAsync(PackFilePath));

    public IAsyncRelayCommand ActivateCommand => new AsyncRelayCommand(ActivateAsync, () => CurrentAppearance is not null);

    public IAsyncRelayCommand UninstallCommand => new AsyncRelayCommand(
        UninstallAsync,
        () => SelectedServant is { IsEmbedded: false } && CurrentAppearance is not null);

    public IAsyncRelayCommand OpenPackFolderCommand => new AsyncRelayCommand(OpenPackFolderAsync, () => SelectedServant is not null);

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
        UninstallCommand.NotifyCanExecuteChanged();
        OpenPackFolderCommand.NotifyCanExecuteChanged();
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
}