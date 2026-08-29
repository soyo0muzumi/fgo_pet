using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Json;

namespace FgoPet.Infrastructure.Settings;

/// <summary>
/// Versioned, atomically-written user settings. Corrupt JSON is quarantined for
/// diagnosis and replaced with defaults in memory.
/// </summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private const int SchemaVersion = 2;
    private readonly string _path;

    public JsonAppSettingsStore(string storageRoot)
    {
        _path = Path.Combine(storageRoot, "settings.json");
    }

    public string Location => _path;

    public AppSettings Load()
    {
        var text = AtomicJson.ReadOrNull(_path);
        if (text is null)
        {
            return AppSettings.Defaults;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(text);
            if (dto is null || dto.SchemaVersion is < 1 or > SchemaVersion)
            {
                AtomicJson.Quarantine(_path);
                return AppSettings.Defaults;
            }

            var settings = new AppSettings(
                dto.Selection?.ToModel(),
                dto.Scale,
                dto.Topmost,
                dto.AutoCollapseExpandedPanel);
            return settings with
            {
                ModelConnection = dto.ModelConnection?.ToModel(),
                MemoryEnabled = dto.MemoryEnabled ?? true,
                ServantPreferences = ParseServantPreferences(dto.ServantPreferences),
                Theme = ParseTheme(dto.Theme),
                UserProfile = dto.UserProfile?.ToModel(),
                PackageSettings = ParsePackageSettings(dto.PackageSettings),
            };
        }
        catch (JsonException)
        {
            AtomicJson.Quarantine(_path);
            return AppSettings.Defaults;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dto = new SettingsDto
        {
            SchemaVersion = SchemaVersion,
            Selection = SelectionDto.FromModel(settings.Selection),
            Scale = settings.Scale,
            Topmost = settings.Topmost,
            AutoCollapseExpandedPanel = settings.AutoCollapseExpandedPanel,
            ModelConnection = ModelConnectionDto.FromModel(settings.ModelConnection),
            MemoryEnabled = settings.MemoryEnabled,
            ServantPreferences = settings.ServantPreferences.ToDictionary(
                pair => pair.Key,
                pair => ServantPreferenceDto.FromModel(pair.Value),
                StringComparer.Ordinal),
            Theme = FormatTheme(settings.Theme),
            UserProfile = UserProfileDto.FromModel(settings.UserProfile),
            PackageSettings = settings.PackageSettings.ToDictionary(
                package => package.Key,
                package => (Dictionary<string, string?>?)package.Value.ToDictionary(
                    setting => setting.Key,
                    setting => (string?)setting.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
        };
        AtomicJson.Write(_path, JsonSerializer.Serialize(dto));
    }

    private sealed record SettingsDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("selection")]
        public SelectionDto? Selection { get; init; }

        [JsonPropertyName("scale")]
        public double Scale { get; init; }

        [JsonPropertyName("topmost")]
        public bool Topmost { get; init; }

        [JsonPropertyName("auto_collapse")]
        public bool AutoCollapseExpandedPanel { get; init; }

        [JsonPropertyName("model_connection")]
        public ModelConnectionDto? ModelConnection { get; init; }

        [JsonPropertyName("memory_enabled")]
        public bool? MemoryEnabled { get; init; }

        [JsonPropertyName("servant_preferences")]
        public Dictionary<string, ServantPreferenceDto>? ServantPreferences { get; init; }

        [JsonPropertyName("theme")]
        public string? Theme { get; init; }

        [JsonPropertyName("user_profile")]
        public UserProfileDto? UserProfile { get; init; }

        [JsonPropertyName("package_settings")]
        public Dictionary<string, Dictionary<string, string?>?>? PackageSettings { get; init; }
    }

    private sealed record UserProfileDto(
        [property: JsonPropertyName("display_name")] string? DisplayName)
    {
        public UserProfile ToModel() => new(DisplayName);

        public static UserProfileDto? FromModel(UserProfile? profile) =>
            profile is null ? null : new UserProfileDto(profile.DisplayName);
    }

    private sealed record ModelConnectionDto(
        [property: JsonPropertyName("provider_id")] string? ProviderId,
        [property: JsonPropertyName("base_url")] string? BaseUrl,
        [property: JsonPropertyName("model_id")] string? ModelId)
    {
        public ModelConnectionSettings? ToModel() =>
            string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ModelId)
                ? null
                : new ModelConnectionSettings(ProviderId, BaseUrl, ModelId);

        public static ModelConnectionDto? FromModel(ModelConnectionSettings? settings) =>
            settings is null ? null : new ModelConnectionDto(settings.ProviderId, settings.BaseUrl, settings.ModelId);
    }

    private sealed record ServantPreferenceDto(
        [property: JsonPropertyName("address_mode")] string? AddressMode,
        [property: JsonPropertyName("address_text")] string? AddressText)
    {
        public ServantPreference ToModel() =>
            AddressMode switch
            {
                "package_default" => new ServantPreference(FgoPet.Core.Settings.AddressMode.PackageDefault),
                "user_defined" => new ServantPreference(FgoPet.Core.Settings.AddressMode.UserDefined, AddressText),
                _ => throw new JsonException("Unknown servant address mode."),
            };

        public static ServantPreferenceDto FromModel(ServantPreference preference) =>
            preference.AddressMode switch
            {
                FgoPet.Core.Settings.AddressMode.PackageDefault => new("package_default", null),
                FgoPet.Core.Settings.AddressMode.UserDefined => new("user_defined", preference.AddressText),
                _ => throw new ArgumentOutOfRangeException(nameof(preference)),
            };
    }

    private static IReadOnlyDictionary<string, ServantPreference> ParseServantPreferences(
        IReadOnlyDictionary<string, ServantPreferenceDto>? preferences)
    {
        if (preferences is null || preferences.Count == 0)
        {
            return AppSettings.Defaults.ServantPreferences;
        }

        var parsed = new Dictionary<string, ServantPreference>(StringComparer.Ordinal);
        foreach (var pair in preferences)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128)
            {
                throw new JsonException("Invalid servant preference key.");
            }

            parsed[pair.Key] = pair.Value.ToModel();
        }

        return parsed;
    }

    private static AppTheme ParseTheme(string? theme) => theme switch
    {
        "fgo_light" => AppTheme.FgoLight,
        "modern_gray" => AppTheme.ModernGray,
        _ => AppTheme.ModernGray,
    };

    private static string FormatTheme(AppTheme theme) => theme switch
    {
        AppTheme.ModernGray => "modern_gray",
        AppTheme.FgoLight => "fgo_light",
        _ => "modern_gray",
    };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParsePackageSettings(
        IReadOnlyDictionary<string, Dictionary<string, string?>?>? packageSettings)
    {
        if (packageSettings is null || packageSettings.Count == 0)
        {
            return AppSettings.Defaults.PackageSettings;
        }

        var parsed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var package in packageSettings)
        {
            if (!IsSafeServantId(package.Key))
            {
                throw new JsonException("Invalid package settings servant key.");
            }
            if (package.Value is null)
            {
                throw new JsonException("Package settings must be an object.");
            }

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var setting in package.Value)
            {
                if (!IsValidPackageSettingKey(setting.Key))
                {
                    throw new JsonException("Invalid package setting key.");
                }
                if (setting.Value is null || setting.Value.Length > 256)
                {
                    throw new JsonException("Invalid package setting value.");
                }

                settings[setting.Key] = setting.Value;
            }

            parsed[package.Key] = settings;
        }

        return parsed;
    }

    private static bool IsSafeServantId(string? value) =>
        value is { Length: > 0 and <= 128 }
        && IsAsciiAlphaNumeric(value[0])
        && value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');

    private static bool IsValidPackageSettingKey(string? value) =>
        value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
                                  or >= '0' and <= '9'
                                  or '.' or '_' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private sealed record SelectionDto(
        [property: JsonPropertyName("package_id")] string? PackageId,
        [property: JsonPropertyName("appearance_id")] string? AppearanceId,
        [property: JsonPropertyName("package_version")] string? PackageVersion)
    {
        public PortraitSelection? ToModel() =>
            string.IsNullOrWhiteSpace(PackageId) || string.IsNullOrWhiteSpace(AppearanceId)
                ? null
                : new PortraitSelection(PackageId, AppearanceId, PackageVersion);

        public static SelectionDto? FromModel(PortraitSelection? selection) =>
            selection is null
                ? null
                : new SelectionDto(selection.PackageId, selection.AppearanceId, selection.PackageVersion);
    }
}
