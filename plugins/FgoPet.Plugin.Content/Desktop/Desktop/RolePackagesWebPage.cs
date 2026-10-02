using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Servants;
using FgoPet.Character.Settings;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.UiSdk;
using Microsoft.Win32;

namespace FgoPet.App.Settings;

/// <summary>Adapts the existing role-package catalog and detail VM to one Settings Web host.</summary>
public sealed class RolePackagesWebPage : ISettingsWebPage
{
    private static readonly string[] PageCommands =
    [
        "rolePackages.get",
        "rolePackages.search",
        "rolePackages.open",
        "rolePackages.back",
        "rolePackages.scan",
        "rolePackages.chooseFile",
        "rolePackages.install",
        "rolePackages.section",
        "rolePackages.selectAppearance",
        "rolePackages.activate",
        "rolePackages.openFolder",
        "rolePackages.uninstall",
        "rolePackages.editAddress",
        "rolePackages.saveAddress",
        "rolePackages.setSetting",
        "rolePackages.saveSettings",
    ];

    private static readonly ConditionalWeakTable<ServantLibraryViewModel, SemaphoreSlim> LibraryGates = new();
    private readonly ICharacterSettingsStore _settings;
    private readonly ServantLibraryViewModel _library;
    private readonly Func<string?> _selectPackageFile;
    private readonly SemaphoreSlim _libraryGate;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly Dictionary<(string PackageId, string Version, string? PreviewPath), string?> _previewCache = [];
    private readonly LocalSettingsNavigator _navigator;
    private RolePackageDetailViewModel? _detail;
    private string? _detailPackageId;
    private string? _selectedArchivePath;
    private string? _selectedArchiveName;
    private string _searchText = string.Empty;
    private Route _route;

    public RolePackagesWebPage(ICharacterSettingsStore settings, ServantLibraryViewModel library)
        : this(settings, library, SelectPackageFile)
    {
    }

    internal RolePackagesWebPage(ICharacterSettingsStore settings, ServantLibraryViewModel library,
        Func<string?> selectPackageFile)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _selectPackageFile = selectPackageFile ?? throw new ArgumentNullException(nameof(selectPackageFile));
        _libraryGate = LibraryGates.GetValue(library, static _ => new SemaphoreSlim(1, 1));
        _navigator = new LocalSettingsNavigator(
            openPackage: route =>
            {
                if (route is not null)
                {
                    _detailPackageId = route.PackageId;
                    _route = Route.Detail;
                }
            },
            backToPackages: () => _route = Route.Catalog);
    }

    public string SettingsPageId => nameof(SettingsSection.RolePackages);

    public string ModulePath => "pages/role-packages.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new RolePackagesWebPage(_settings, _library, _selectPackageFile);

    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return message.Type switch
            {
                "rolePackages.get" => await GetAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.search" => Search(message.Payload),
                "rolePackages.open" => await OpenAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.back" => await BackAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.scan" => await ScanAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.chooseFile" => ChooseFile(message.Payload, cancellationToken),
                "rolePackages.install" => await InstallAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.section" => await SelectSectionAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.selectAppearance" => await SelectAppearanceAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.activate" => await ActivateAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.openFolder" => await OpenFolderAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.uninstall" => await UninstallAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.editAddress" => await EditAddressAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.saveAddress" => await SaveAddressAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.setSetting" => await SetSettingAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "rolePackages.saveSettings" => await SaveSettingsAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                _ => Failure("SETTINGS_UNKNOWN_COMMAND"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("SETTINGS_UNAVAILABLE");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<WebSurfaceCommandResult> GetAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId"], out _))
            return Failure("SETTINGS_INVALID_INPUT");

        if (_route == Route.Catalog)
        {
            await RefreshCatalogAsync(cancellationToken).ConfigureAwait(true);
        }
        else if (_detail is not null)
        {
            await WithLibraryGateAsync(async () =>
            {
                await _detail.LoadAsync(cancellationToken).ConfigureAwait(true);
            }, cancellationToken).ConfigureAwait(true);
        }

        return Success(CreateSnapshot());
    }

    private WebSurfaceCommandResult Search(JsonElement payload)
    {
        if (!TryValidatePayload(payload, ["pageId", "query"], out _)
            || !TryReadString(payload, "query", 256, out var query)
            || _route != Route.Catalog)
            return Failure("SETTINGS_INVALID_INPUT");

        _searchText = query;
        return Success(CreateSnapshot());
    }

    private async Task<WebSurfaceCommandResult> OpenAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId)
            || _route != Route.Catalog)
            return Failure("SETTINGS_INVALID_INPUT");

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _library.LoadAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var card = _library.Servants.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageId, packageId, StringComparison.Ordinal));
            if (card is null)
                return Failure("SETTINGS_UNAVAILABLE");

            var detail = _detail;
            if (detail is null || !string.Equals(_detailPackageId, packageId, StringComparison.Ordinal))
            {
                detail = new RolePackageDetailViewModel(
                    new PackageDetailRoute(packageId, card.DisplayName),
                    _library,
                    _settings,
                    _navigator);
            }

            await detail.LoadAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!detail.IsAvailable)
                return Failure("SETTINGS_UNAVAILABLE");

            _detail = detail;
            _detailPackageId = packageId;
            _route = Route.Detail;
            return Success(CreateSnapshot());
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    private async Task<WebSurfaceCommandResult> BackAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId"], out _))
            return Failure("SETTINGS_INVALID_INPUT");
        if (_route != Route.Detail)
            return Failure("SETTINGS_INVALID_INPUT");

        await RefreshCatalogAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        _route = Route.Catalog;
        return Success(CreateSnapshot());
    }

    private async Task<WebSurfaceCommandResult> ScanAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId"], out _) || _route != Route.Catalog)
            return Failure("SETTINGS_INVALID_INPUT");

        _previewCache.Clear();
        await RefreshCatalogAsync(cancellationToken).ConfigureAwait(true);
        return Success(CreateSnapshot());
    }

    private WebSurfaceCommandResult ChooseFile(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId"], out _) || _route != Route.Catalog)
            return Failure("SETTINGS_INVALID_INPUT");
        cancellationToken.ThrowIfCancellationRequested();

        var path = _selectPackageFile();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            return Success(CreateSnapshot());

        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".fgopetpack", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileName(path)))
            return Failure("SETTINGS_INVALID_INPUT");

        _selectedArchivePath = path;
        _selectedArchiveName = Path.GetFileName(path);
        return Success(CreateSnapshot());
    }

    private async Task<WebSurfaceCommandResult> InstallAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId"], out _) || _route != Route.Catalog)
            return Failure("SETTINGS_INVALID_INPUT");
        if (_selectedArchivePath is null)
            return Failure("SETTINGS_INVALID_INPUT");

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The installer and its catalog refresh are an existing non-cancellable commit.
            // Once started, return its completed diagnostic/snapshot even if the host closes.
            await _library.InstallAsync(_selectedArchivePath).ConfigureAwait(true);
            _previewCache.Clear();
        }
        finally
        {
            _libraryGate.Release();
        }
        return Success(CreateSnapshot());
    }

    private async Task<WebSurfaceCommandResult> SelectSectionAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "section"], out _)
            || !TryReadString(payload, "section", 32, out var value))
            return Failure("SETTINGS_INVALID_INPUT");

        var section = value switch
        {
            "appearance" => RolePackageDetailSection.Appearance,
            "address" => RolePackageDetailSection.Address,
            "packageInfo" => RolePackageDetailSection.PackageInfo,
            _ => (RolePackageDetailSection?)null,
        };
        if (section is null)
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(_detailPackageId, cancellationToken, detail =>
        {
            detail.SelectSection(section.Value);
            return Task.FromResult(Success(CreateSnapshot()));
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> SelectAppearanceAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId", "appearanceId", "packageVersion"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId)
            || !TryReadString(payload, "appearanceId", 128, out var appearanceId)
            || !TryReadString(payload, "packageVersion", 64, out var packageVersion))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, detail =>
        {
            var appearance = FindAppearance(detail, appearanceId, packageVersion);
            if (appearance is null)
                return Task.FromResult(Failure("SETTINGS_INVALID_INPUT"));
            detail.SelectedAppearance = appearance;
            return Task.FromResult(Success(CreateSnapshot()));
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> ActivateAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId", "appearanceId", "packageVersion"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId)
            || !TryReadString(payload, "appearanceId", 128, out var appearanceId)
            || !TryReadString(payload, "packageVersion", 64, out var packageVersion))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, async detail =>
        {
            var appearance = FindAppearance(detail, appearanceId, packageVersion);
            if (appearance is null)
                return Failure("SETTINGS_INVALID_INPUT");
            detail.SelectedAppearance = appearance;
            cancellationToken.ThrowIfCancellationRequested();
            await detail.ActivateAsync().ConfigureAwait(true);
            return Success(CreateSnapshot());
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> OpenFolderAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId))
            return Failure("SETTINGS_INVALID_INPUT");

        if (_route == Route.Detail)
        {
            return await WithDetailAsync(packageId, cancellationToken, async detail =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await detail.OpenPackFolderAsync().ConfigureAwait(true);
                return Success(CreateSnapshot());
            }).ConfigureAwait(true);
        }

        if (_route != Route.Catalog)
            return Failure("SETTINGS_INVALID_INPUT");

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _library.LoadAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            var card = _library.Servants.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageId, packageId, StringComparison.Ordinal));
            if (card is null)
                return Failure("SETTINGS_INVALID_INPUT");
            _library.SelectedServant = card;
            cancellationToken.ThrowIfCancellationRequested();
            await _library.OpenPackFolderAsync().ConfigureAwait(true);
            return Success(CreateSnapshot());
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    private async Task<WebSurfaceCommandResult> UninstallAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId", "appearanceId", "packageVersion"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId)
            || !TryReadString(payload, "appearanceId", 128, out var appearanceId)
            || !TryReadString(payload, "packageVersion", 64, out var packageVersion))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, async detail =>
        {
            var card = _library.Servants.FirstOrDefault(candidate =>
                string.Equals(candidate.PackageId, packageId, StringComparison.Ordinal));
            var appearance = FindAppearance(detail, appearanceId, packageVersion);
            if (card is null || card.IsEmbedded || appearance is null
                || detail.SelectedAppearance is null
                || !string.Equals(detail.SelectedAppearance.AppearanceId, appearanceId, StringComparison.Ordinal)
                || !string.Equals(detail.SelectedAppearance.PackageVersion, packageVersion, StringComparison.Ordinal))
                return Failure("SETTINGS_INVALID_INPUT");

            cancellationToken.ThrowIfCancellationRequested();
            await detail.UninstallAsync().ConfigureAwait(true);
            if (_route == Route.Catalog)
            {
                _detail = null;
                _detailPackageId = null;
                _previewCache.Clear();
                return Success(CreateSnapshot());
            }

            return Success(CreateSnapshot());
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> EditAddressAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId", "mode", "value"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId)
            || !TryReadString(payload, "mode", 32, out var mode)
            || !TryReadString(payload, "value", 256, out var value)
            || mode is not ("packageDefault" or "custom")
            || (mode == "packageDefault" && value.Length != 0))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, detail =>
        {
            detail.UsePackageDefaultAddress = mode == "packageDefault";
            detail.UseCustomAddress = mode == "custom";
            if (mode == "custom")
                detail.CustomAddress = value;
            return Task.FromResult(Success(CreateSnapshot()));
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> SaveAddressAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, async detail =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await detail.SaveAddressAsync().ConfigureAwait(true);
            return Success(CreateSnapshot());
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> SetSettingAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId", "key", "value"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId)
            || !TryReadString(payload, "key", 128, out var key)
            || !TryReadString(payload, "value", 256, out var value))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, detail =>
        {
            var setting = detail.PackageSettings.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.Ordinal));
            if (setting is null || !setting.Definition.IsValidStoredValue(value))
                return Task.FromResult(Failure("SETTINGS_INVALID_INPUT"));
            setting.Value = value;
            return Task.FromResult(Success(CreateSnapshot()));
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> SaveSettingsAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePayload(payload, ["pageId", "packageId"], out _)
            || !TryReadString(payload, "packageId", 128, out var packageId))
            return Failure("SETTINGS_INVALID_INPUT");

        return await WithDetailAsync(packageId, cancellationToken, detail =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            detail.SavePackageSettings();
            return Task.FromResult(Success(CreateSnapshot()));
        }).ConfigureAwait(true);
    }

    private async Task<WebSurfaceCommandResult> WithDetailAsync(string? packageId,
        CancellationToken cancellationToken,
        Func<RolePackageDetailViewModel, Task<WebSurfaceCommandResult>> operation)
    {
        if (_route != Route.Detail || _detail is null || string.IsNullOrWhiteSpace(_detailPackageId)
            || packageId is null || !string.Equals(packageId, _detailPackageId, StringComparison.Ordinal))
            return Failure("SETTINGS_INVALID_INPUT");

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _detail.LoadAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(_detail.PackageId, packageId, StringComparison.Ordinal))
                return Failure("SETTINGS_INVALID_INPUT");
            if (!_detail.IsAvailable)
                return Success(CreateSnapshot());
            return await operation(_detail).ConfigureAwait(true);
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    private async Task RefreshCatalogAsync(CancellationToken cancellationToken) =>
        await WithLibraryGateAsync(() => _library.LoadAsync(), cancellationToken).ConfigureAwait(true);

    private async Task WithLibraryGateAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    private object CreateSnapshot()
    {
        var diagnostic = CurrentDiagnostic();
        var diagnosticSnapshot = diagnostic is null
            ? null
            : new { heading = diagnostic.Heading, text = diagnostic.Text };

        if (_route == Route.Catalog)
        {
            var query = _searchText.Trim();
            var packages = _library.Servants
                .Where(card => query.Length == 0
                    || card.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || card.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(card => new
                {
                    packageId = card.PackageId,
                    displayName = card.DisplayName,
                    packageVersion = card.PackageVersion,
                    sourceBadge = card.SourceBadge,
                    isActive = card.IsActive,
                    isEmbedded = card.IsEmbedded,
                    appearanceCount = card.Appearances.Count,
                    previewDataUrl = PreviewFor(card.PackageId, card.PackageVersion, card.PreviewSource),
                })
                .ToArray();

            return new
            {
                route = "catalog",
                isBusy = false,
                diagnostic = diagnosticSnapshot,
                catalog = new
                {
                    packages,
                    searchText = _searchText,
                    selectedArchiveName = _selectedArchiveName,
                    scanStatus = _library.ScanStatus,
                },
            };
        }

        var detail = _detail;
        if (detail is null)
        {
            return new
            {
                route = "detail",
                isBusy = false,
                diagnostic = diagnosticSnapshot,
                detail = (object?)null,
            };
        }

        var selectedAppearance = detail.SelectedAppearance;
        return new
        {
            route = "detail",
            isBusy = false,
            diagnostic = diagnosticSnapshot,
            detail = new
            {
                packageId = detail.PackageId,
                displayName = detail.DisplayName,
                packageVersion = detail.PackageVersion,
                sourceBadge = detail.SourceBadge,
                compatibilityText = detail.CompatibilityText,
                packageSummary = detail.PackageSummary,
                previewDataUrl = PreviewFor(detail.PackageId, detail.PackageVersion, detail.PreviewSource),
                isActive = detail.IsActive,
                isEmbedded = _library.Servants.FirstOrDefault(card =>
                    string.Equals(card.PackageId, detail.PackageId, StringComparison.Ordinal))?.IsEmbedded ?? false,
                isAvailable = detail.IsAvailable,
                availabilityStatus = detail.IsAvailable ? string.Empty : detail.PackageSettingsStatus,
                section = SectionToken(detail.SelectedSection),
                appearances = detail.Appearances.Select(appearance => new
                {
                    appearanceId = appearance.AppearanceId,
                    packageVersion = appearance.PackageVersion,
                    display = appearance.Display,
                    isCurrent = appearance.IsCurrent,
                }).ToArray(),
                selectedAppearance = selectedAppearance is null
                    ? null
                    : new { appearanceId = selectedAppearance.AppearanceId, packageVersion = selectedAppearance.PackageVersion },
                address = new
                {
                    defaultText = detail.DefaultAddressText,
                    mode = detail.UseCustomAddress ? "custom" : "packageDefault",
                    customText = detail.CustomAddress,
                    status = detail.AddressStatus,
                },
                settings = detail.PackageSettings.Select(setting => new
                {
                    key = setting.Key,
                    label = setting.Label,
                    type = SettingTypeToken(setting.Type),
                    value = setting.Value,
                    options = setting.Options,
                }).ToArray(),
                migrationNotice = detail.MigrationNotice,
                packageSettingsStatus = detail.PackageSettingsStatus,
                addressStatus = detail.AddressStatus,
            },
        };
    }

    private PackageDiagnosticViewModel? CurrentDiagnostic() => _route == Route.Detail
        ? _detail?.Diagnostic ?? _library.Diagnostic
        : _library.Diagnostic;

    private string? PreviewFor(string packageId, string version, string? previewPath)
    {
        var key = (packageId, version, previewPath);
        if (!_previewCache.TryGetValue(key, out var result))
        {
            result = PersonalizationWebPage.TryCreatePreviewDataUrl(previewPath);
            _previewCache[key] = result;
        }

        return result;
    }

    private static ServantAppearanceItemViewModel? FindAppearance(RolePackageDetailViewModel detail,
        string appearanceId, string packageVersion) => detail.Appearances.FirstOrDefault(candidate =>
        string.Equals(candidate.AppearanceId, appearanceId, StringComparison.Ordinal)
        && string.Equals(candidate.PackageVersion, packageVersion, StringComparison.Ordinal));

    private static string SectionToken(RolePackageDetailSection section) => section switch
    {
        RolePackageDetailSection.Appearance => "appearance",
        RolePackageDetailSection.Address => "address",
        RolePackageDetailSection.PackageInfo => "packageInfo",
        _ => "appearance",
    };

    private static string SettingTypeToken(PackSettingType type) => type switch
    {
        PackSettingType.Toggle => "toggle",
        PackSettingType.Choice => "choice",
        PackSettingType.Text => "text",
        _ => string.Empty,
    };

    private bool TryValidatePayload(JsonElement payload, IReadOnlyList<string> allowedProperties, out string pageId)
    {
        pageId = string.Empty;
        return HasOnlyProperties(payload, allowedProperties)
            && TryReadString(payload, "pageId", 64, out pageId)
            && string.Equals(pageId, SettingsPageId, StringComparison.Ordinal);
    }

    private static bool HasOnlyProperties(JsonElement payload, IReadOnlyList<string> allowedProperties)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }

        return seen.Count == allowed.Count && allowed.All(seen.Contains);
    }

    private static bool TryReadString(JsonElement payload, string propertyName, int maxLength, out string value)
    {
        value = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;

        var candidate = property.GetString();
        if (candidate is null || candidate.Length > maxLength || candidate.Any(char.IsControl))
            return false;

        if ((propertyName is "pageId" or "packageId" or "appearanceId" or "packageVersion" or "key")
            && string.IsNullOrWhiteSpace(candidate))
            return false;

        value = candidate;
        return true;
    }

    private static string? SelectPackageFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "FGO Pet role package (*.fgopetpack)|*.fgopetpack",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static WebSurfaceCommandResult Success(object payload) => new(true, payload);

    private static WebSurfaceCommandResult Failure(string errorCode) => new(false, ErrorCode: errorCode);

    private enum Route
    {
        Catalog,
        Detail,
    }

    private sealed class LocalSettingsNavigator(Action<PackageDetailRoute?> openPackage, Action backToPackages)
        : ISettingsNavigator
    {
        public IRelayCommand<PackageDetailRoute> OpenPackageCommand { get; } =
            new RelayCommand<PackageDetailRoute>(route => openPackage(route));

        public IRelayCommand BackToPackagesCommand { get; } = new RelayCommand(backToPackages);

        public IRelayCommand BackToAppearanceCommand { get; } = new RelayCommand(() => { });

        public void Select(SettingsSection section) { }
    }
}
