using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Portraits;

namespace FgoPet.Infrastructure.Packs;

public sealed record PackIndexV1(PortraitSelection? Selected, PortraitSelection? LastKnownGood)
{
    public static PackIndexV1 Empty { get; } = new(null, null);
}

public interface IPackIndexStore
{
    string Location { get; }
    PackIndexV1 Load();
    void Save(PackIndexV1 index);
}

/// <summary>
/// Persists the preferred selection and last-known-good selection as atomic, versioned
/// JSON. A corrupt file is renamed for diagnosis (quarantined) and replaced with the
/// default value in memory.
/// </summary>
public sealed class JsonPackIndexStore : IPackIndexStore
{
    private const int SchemaVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _path;

    public JsonPackIndexStore(string storageRoot)
    {
        ArgumentNullException.ThrowIfNull(storageRoot);
        _path = Path.Combine(storageRoot, "state", "index.json");
    }

    public string Location => _path;

    public PackIndexV1 Load()
    {
        if (!File.Exists(_path))
        {
            return PackIndexV1.Empty;
        }

        try
        {
            var json = File.ReadAllText(_path, Utf8);
            var dto = JsonSerializer.Deserialize<IndexDto>(json);
            if (dto is null || dto.SchemaVersion != SchemaVersion)
            {
                Quarantine();
                return PackIndexV1.Empty;
            }
            return new PackIndexV1(dto.Selected?.ToModel(), dto.LastKnownGood?.ToModel());
        }
        catch (JsonException)
        {
            Quarantine();
            return PackIndexV1.Empty;
        }
        catch (IOException)
        {
            // Unreadable but not malformed: keep the file, fall back to defaults in memory.
            return PackIndexV1.Empty;
        }
    }

    public void Save(PackIndexV1 index)
    {
        var dto = new IndexDto
        {
            SchemaVersion = SchemaVersion,
            Selected = PortraitSelectionDto.FromModel(index.Selected),
            LastKnownGood = PortraitSelectionDto.FromModel(index.LastKnownGood),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(dto);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, Utf8.GetBytes(json));
        File.Move(temp, _path, overwrite: true);
    }

    private void Quarantine()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(_path, $"{_path}.corrupt.{stamp}");
        }
        catch (IOException)
        {
            // best effort quarantine
        }
    }

    private sealed record IndexDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("selected")]
        public PortraitSelectionDto? Selected { get; init; }

        [JsonPropertyName("last_known_good")]
        public PortraitSelectionDto? LastKnownGood { get; init; }
    }

    private sealed record PortraitSelectionDto(
        [property: JsonPropertyName("package_id")] string? PackageId,
        [property: JsonPropertyName("appearance_id")] string? AppearanceId,
        [property: JsonPropertyName("package_version")] string? PackageVersion)
    {
        public PortraitSelection? ToModel() =>
            string.IsNullOrWhiteSpace(PackageId) || string.IsNullOrWhiteSpace(AppearanceId)
                ? null
                : new PortraitSelection(PackageId, AppearanceId, PackageVersion);

        public static PortraitSelectionDto? FromModel(PortraitSelection? selection) =>
            selection is null
                ? null
                : new PortraitSelectionDto(selection.PackageId, selection.AppearanceId, selection.PackageVersion);
    }
}