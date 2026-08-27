using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Json;

namespace FgoPet.Infrastructure.Windowing;

/// <summary>
/// Versioned, atomically-written transient window placement. Corrupt JSON is
/// quarantined and treated as no placement.
/// </summary>
public sealed class JsonWindowPlacementStore : IWindowPlacementStore
{
    private const int SchemaVersion = 1;
    private readonly string _path;

    public JsonWindowPlacementStore(string storageRoot)
    {
        _path = Path.Combine(storageRoot, "window-placement.json");
    }

    public string Location => _path;

    public WindowPlacement? Load()
    {
        var text = AtomicJson.ReadOrNull(_path);
        if (text is null)
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PlacementDto>(text);
            if (dto is null || dto.SchemaVersion != SchemaVersion)
            {
                AtomicJson.Quarantine(_path);
                return null;
            }

            return new WindowPlacement(
                dto.MonitorId,
                dto.OffsetX,
                dto.OffsetY,
                dto.SavedDpiX,
                dto.SavedDpiY,
                dto.WindowWidthDip,
                dto.WindowHeightDip);
        }
        catch (JsonException)
        {
            AtomicJson.Quarantine(_path);
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var dto = new PlacementDto
        {
            SchemaVersion = SchemaVersion,
            MonitorId = placement.MonitorId,
            OffsetX = placement.OffsetX,
            OffsetY = placement.OffsetY,
            SavedDpiX = placement.SavedDpiX,
            SavedDpiY = placement.SavedDpiY,
            WindowWidthDip = placement.WindowWidthDip,
            WindowHeightDip = placement.WindowHeightDip,
        };
        AtomicJson.Write(_path, JsonSerializer.Serialize(dto));
    }

    private sealed record PlacementDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("monitor_id")]
        public string? MonitorId { get; init; }

        [JsonPropertyName("offset_x")]
        public double OffsetX { get; init; }

        [JsonPropertyName("offset_y")]
        public double OffsetY { get; init; }

        [JsonPropertyName("saved_dpi_x")]
        public double SavedDpiX { get; init; }

        [JsonPropertyName("saved_dpi_y")]
        public double SavedDpiY { get; init; }

        [JsonPropertyName("window_width_dip")]
        public double WindowWidthDip { get; init; }

        [JsonPropertyName("window_height_dip")]
        public double WindowHeightDip { get; init; }
    }
}