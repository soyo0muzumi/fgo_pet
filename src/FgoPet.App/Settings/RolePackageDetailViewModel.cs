using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Servants;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;

namespace FgoPet.App.Settings;

public enum RolePackageDetailSection
{
    Appearance,
    Address,
    PackageInfo,
}

public sealed record RolePackageDetailNavigationItem(
    RolePackageDetailSection Section,
    string Label,
    string IconKey);

public sealed partial class RolePackageSettingViewModel : ObservableObject
{
    public RolePackageSettingViewModel(PackSettingDefinition definition, string value)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _value = value;
    }

    public PackSettingDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public PackSettingType Type => Definition.Type;
    public IReadOnlyList<string> Options => Definition.Options ?? [];
    public bool IsToggle => Type == PackSettingType.Toggle;
    public bool IsChoice => Type == PackSettingType.Choice;
    public bool IsText => Type == PackSettingType.Text;

    [ObservableProperty]
    private string _value;

    public bool ToggleValue
    {
        get => Value == "true";
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(ToggleValue));
}

/// <summary>
/// Adapts one role-package route to the existing library operations while keeping
/// servant-owned address and package values independent from global profile data.
/// </summary>
public sealed partial class RolePackageDetailViewModel : ObservableObject
{
    private static readonly IReadOnlyList<RolePackageDetailNavigationItem> Navigation =
    [
        new(RolePackageDetailSection.Appearance, "从者与外观", "IconAppearanceGeometry"),
        new(RolePackageDetailSection.Address, "称呼设置", "IconAddressGeometry"),
        new(RolePackageDetailSection.PackageInfo, "角色包信息", "IconPackageInfoGeometry"),
    ];

    private readonly PackageDetailRoute _route;
    private readonly ServantLibraryViewModel _library;
    private readonly IAppSettingsStore _settings;
    private readonly ServantPreferenceService _preferences;
    private readonly SettingsViewModel _settingsShell;
    private ServantCardViewModel? _card;
    private string? _loadedPackageVersion;
    private bool _addressLoaded;

    public RolePackageDetailViewModel(
        PackageDetailRoute route,
        ServantLibraryViewModel library,
        IAppSettingsStore settings,
        SettingsViewModel settingsShell)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _preferences = new ServantPreferenceService(_settings);
        _settingsShell = settingsShell ?? throw new ArgumentNullException(nameof(settingsShell));

        ActivateCommand = new AsyncRelayCommand(ActivateAsync, () => SelectedAppearance is not null && IsAvailable);
        SaveAddressCommand = new AsyncRelayCommand(SaveAddressAsync, () => IsAvailable);
        SavePackageSettingsCommand = new RelayCommand(SavePackageSettings, () => IsAvailable);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => IsAvailable && _card is { IsEmbedded: false });
        OpenPackFolderCommand = new AsyncRelayCommand(OpenPackFolderAsync, () => IsAvailable);
        BackCommand = _settingsShell.BackToPackagesCommand;
    }

    public IReadOnlyList<RolePackageDetailNavigationItem> NavigationItems => Navigation;

    public string PackageId => _route.PackageId;

    [ObservableProperty]
    private string _servantId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _packageVersion = string.Empty;

    [ObservableProperty]
    private string _sourceBadge = string.Empty;

    [ObservableProperty]
    private string _compatibilityText = string.Empty;

    [ObservableProperty]
    private string? _previewSource;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private IReadOnlyList<ServantAppearanceItemViewModel> _appearances = Array.Empty<ServantAppearanceItemViewModel>();

    [ObservableProperty]
    private ServantAppearanceItemViewModel? _selectedAppearance;

    [ObservableProperty]
    private RolePackageDetailSection _selectedSection = RolePackageDetailSection.Appearance;

    public bool IsAppearanceSection => SelectedSection == RolePackageDetailSection.Appearance;
    public bool IsAddressSection => SelectedSection == RolePackageDetailSection.Address;
    public bool IsPackageInfoSection => SelectedSection == RolePackageDetailSection.PackageInfo;

    [ObservableProperty]
    private bool _usePackageDefaultAddress = true;

    [ObservableProperty]
    private bool _useCustomAddress;

    [ObservableProperty]
    private string _customAddress = string.Empty;

    [ObservableProperty]
    private string _addressStatus = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<RolePackageSettingViewModel> _packageSettings = Array.Empty<RolePackageSettingViewModel>();

    [ObservableProperty]
    private string _packageSettingsStatus = string.Empty;

    [ObservableProperty]
    private string _migrationNotice = string.Empty;

    public bool IsMigrationNoticeVisible => MigrationNotice.Length > 0;

    [ObservableProperty]
    private PackageDiagnosticViewModel? _diagnostic;

    public bool IsDiagnosticVisible => Diagnostic is not null;

    public IAsyncRelayCommand ActivateCommand { get; }
    public IAsyncRelayCommand SaveAddressCommand { get; }
    public IRelayCommand SavePackageSettingsCommand { get; }
    public IAsyncRelayCommand UninstallCommand { get; }
    public IAsyncRelayCommand OpenPackFolderCommand { get; }
    public IRelayCommand BackCommand { get; }

    public async Task LoadAsync()
    {
        await _library.LoadAsync();
        var card = _library.Servants.FirstOrDefault(candidate => candidate.PackageId == _route.PackageId);
        if (card is null)
        {
            _card = null;
            IsAvailable = false;
            DisplayName = _route.DisplayName;
            PackageSettingsStatus = "角色包已不可用，请返回列表重新扫描。";
            NotifyCommandState();
            return;
        }

        (string AppearanceId, string PackageVersion)? previousAppearance = SelectedAppearance is null
            ? null
            : (SelectedAppearance.AppearanceId, SelectedAppearance.PackageVersion);
        _card = card;
        IsAvailable = true;
        ServantId = card.ServantId;
        DisplayName = card.DisplayName;
        PackageVersion = card.PackageVersion;
        SourceBadge = card.SourceBadge;
        CompatibilityText = string.IsNullOrWhiteSpace(card.MinAppVersion)
            ? "未声明最低应用版本"
            : $"要求 FGO Pet {card.MinAppVersion} 或更高版本";
        PreviewSource = card.PreviewSource;
        IsActive = card.IsActive;
        Appearances = card.Appearances;
        SelectedAppearance = previousAppearance is null
            ? card.SelectedAppearance
            : card.Appearances.FirstOrDefault(item =>
                item.AppearanceId == previousAppearance.Value.AppearanceId &&
                item.PackageVersion == previousAppearance.Value.PackageVersion)
                ?? card.SelectedAppearance;

        if (!_addressLoaded)
        {
            await LoadAddressAsync();
            _addressLoaded = true;
        }

        if (_loadedPackageVersion != card.PackageVersion)
        {
            LoadAndRevalidatePackageSettings(card);
            _loadedPackageVersion = card.PackageVersion;
        }

        Diagnostic = _library.Diagnostic;
        NotifyCommandState();
    }

    public void SelectSection(RolePackageDetailSection section)
    {
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        SelectedSection = section;
    }

    public async Task ActivateAsync()
    {
        if (_card is null || SelectedAppearance is null) return;
        _library.SelectedServant = _card;
        _library.CurrentAppearance = SelectedAppearance;
        await _library.ActivateAsync();
        Diagnostic = _library.Diagnostic;
        IsActive = _card.IsActive;
    }

    public async Task SaveAddressAsync()
    {
        if (!IsAvailable) return;
        if (UseCustomAddress && string.IsNullOrWhiteSpace(CustomAddress))
        {
            AddressStatus = "请输入自定义称呼。";
            return;
        }

        var preference = UseCustomAddress
            ? new ServantPreference(AddressMode.UserDefined, CustomAddress.Trim())
            : new ServantPreference(AddressMode.PackageDefault);
        await _preferences.SaveAsync(ServantId, preference);
        AddressStatus = "称呼已保存。";
    }

    public void SavePackageSettings()
    {
        if (!IsAvailable) return;
        if (PackageSettings.Any(setting => !setting.Definition.IsValidStoredValue(setting.Value)))
        {
            PackageSettingsStatus = "角色包设置值无效，未保存。";
            return;
        }

        var snapshot = _settings.Load();
        var packageSettings = snapshot.PackageSettings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        packageSettings[ServantId] = PackageSettings.ToDictionary(
            setting => setting.Key,
            setting => setting.Value,
            StringComparer.Ordinal);
        _settings.Save(snapshot with { PackageSettings = packageSettings });
        PackageSettingsStatus = "角色包设置已保存。";
    }

    public async Task UninstallAsync()
    {
        if (_card is null || SelectedAppearance is null) return;
        _library.SelectedServant = _card;
        _library.CurrentAppearance = SelectedAppearance;
        await _library.UninstallAsync();
        Diagnostic = _library.Diagnostic;
        if (Diagnostic is null)
        {
            _settingsShell.BackToPackagesCommand.Execute(null);
        }
    }

    public async Task OpenPackFolderAsync()
    {
        if (_card is null) return;
        _library.SelectedServant = _card;
        await _library.OpenPackFolderAsync();
        Diagnostic = _library.Diagnostic;
    }

    partial void OnSelectedAppearanceChanged(ServantAppearanceItemViewModel? value) =>
        ActivateCommand.NotifyCanExecuteChanged();

    partial void OnSelectedSectionChanged(RolePackageDetailSection value)
    {
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsAddressSection));
        OnPropertyChanged(nameof(IsPackageInfoSection));
    }

    partial void OnUsePackageDefaultAddressChanged(bool value)
    {
        if (value) UseCustomAddress = false;
    }

    partial void OnUseCustomAddressChanged(bool value)
    {
        if (value) UsePackageDefaultAddress = false;
    }

    partial void OnMigrationNoticeChanged(string value) =>
        OnPropertyChanged(nameof(IsMigrationNoticeVisible));

    partial void OnDiagnosticChanged(PackageDiagnosticViewModel? value) =>
        OnPropertyChanged(nameof(IsDiagnosticVisible));

    private async Task LoadAddressAsync()
    {
        var preference = await _preferences.LoadAsync(ServantId);
        UsePackageDefaultAddress = preference.AddressMode == AddressMode.PackageDefault;
        UseCustomAddress = preference.AddressMode == AddressMode.UserDefined;
        CustomAddress = preference.AddressText ?? string.Empty;
        AddressStatus = string.Empty;
    }

    private void LoadAndRevalidatePackageSettings(ServantCardViewModel card)
    {
        var snapshot = _settings.Load();
        var hasSaved = snapshot.PackageSettings.TryGetValue(card.ServantId, out var savedValues);
        savedValues ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = card.Settings;
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var migrated = hasSaved && savedValues.Keys.Any(key => definitions.All(definition => definition.Key != key));

        foreach (var definition in definitions)
        {
            if (savedValues.TryGetValue(definition.Key, out var savedValue) && definition.IsValidStoredValue(savedValue))
            {
                normalized[definition.Key] = savedValue;
            }
            else
            {
                normalized[definition.Key] = definition.Default;
                migrated |= hasSaved && savedValues.ContainsKey(definition.Key);
            }
        }

        PackageSettings = definitions
            .Select(definition => new RolePackageSettingViewModel(definition, normalized[definition.Key]))
            .ToArray();
        MigrationNotice = migrated
            ? "角色包升级后，已将不再有效的设置恢复为声明的默认值。"
            : string.Empty;
        PackageSettingsStatus = string.Empty;

        if (!hasSaved && normalized.Count == 0)
        {
            return;
        }

        var settingsChanged = !hasSaved ||
            savedValues.Count != normalized.Count ||
            normalized.Any(pair => !savedValues.TryGetValue(pair.Key, out var value) || value != pair.Value);
        if (!settingsChanged)
        {
            return;
        }

        var allPackageSettings = snapshot.PackageSettings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        if (normalized.Count == 0)
        {
            allPackageSettings.Remove(card.ServantId);
        }
        else
        {
            allPackageSettings[card.ServantId] = normalized;
        }
        _settings.Save(snapshot with { PackageSettings = allPackageSettings });
    }

    private void NotifyCommandState()
    {
        ActivateCommand.NotifyCanExecuteChanged();
        SaveAddressCommand.NotifyCanExecuteChanged();
        SavePackageSettingsCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        OpenPackFolderCommand.NotifyCanExecuteChanged();
    }
}
