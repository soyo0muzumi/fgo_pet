using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Json;

namespace FgoPet.Infrastructure.Windowing;

/// <summary>
/// Versioned, atomically-written transient window placement. Corrupt JSON is
/// quarantined and treated as no placement. The portrait placement keeps the
/// legacy file name; every other slot gets its own sidecar file.
/// </summary>
public sealed class JsonWindowPlacementStore : IWindowPlacementStore
{
    private const int SchemaVersion = 1;
    private readonly string _storageRoot;

    public JsonWindowPlacementStore(string storageRoot)
    {
        _storageRoot = storageRoot;
    }

    public string Location => Path.Combine(_storageRoot, FileName(null));

    public WindowPlacement? Load() => Load(WindowPlacementSlots.Portrait);

    public void Save(WindowPlacement placement) => Save(WindowPlacementSlots.Portrait, placement);

    public WindowPlacement? Load(string slot)
    {
        var path = Path.Combine(_storageRoot, FileName(slot));
        var text = AtomicJson.ReadOrNull(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PlacementDto>(text);
            if (dto is null || dto.SchemaVersion != SchemaVersion)
            {
                AtomicJson.Quarantine(path);
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
            AtomicJson.Quarantine(path);
            return null;
        }
    }

    public void Save(string slot, WindowPlacement placement)
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
        AtomicJson.Write(Path.Combine(_storageRoot, FileName(slot)), JsonSerializer.Serialize(dto));
    }

    private static string FileName(string? slot) => slot is null or WindowPlacementSlots.Portrait
        ? "window-placement.json"
        : $"window-placement.{slot}.json";

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
