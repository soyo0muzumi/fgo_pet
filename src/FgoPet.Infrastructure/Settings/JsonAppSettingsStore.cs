using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const int SchemaVersion = 1;
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
            if (dto is null || dto.SchemaVersion != SchemaVersion)
            {
                AtomicJson.Quarantine(_path);
                return AppSettings.Defaults;
            }

            return new AppSettings(
                dto.Selection?.ToModel(),
                dto.Scale,
                dto.Topmost,
                dto.AutoCollapseExpandedPanel);
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
    }

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