using System.IO;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Windowing;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Windowing;

public sealed class WindowPlacementTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "fgo-pet-placement-" + Guid.NewGuid().ToString("N"));
    private readonly JsonWindowPlacementStore _store;

    public WindowPlacementTests()
    {
        Directory.CreateDirectory(_storage);
        _store = new JsonWindowPlacementStore(_storage);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_storage, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void Load_returns_null_when_missing()
    {
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Save_then_Load_roundtrips_every_field()
    {
        _store.Save(new WindowPlacement("\\\\.\\DISPLAY1", 120.5, 240.0, 2.0, 2.0, 304.0, 604.0));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("\\\\.\\DISPLAY1", loaded!.MonitorId);
        Assert.Equal(120.5, loaded.OffsetX);
        Assert.Equal(240.0, loaded.OffsetY);
        Assert.Equal(2.0, loaded.SavedDpiX);
        Assert.Equal(2.0, loaded.SavedDpiY);
        Assert.Equal(304.0, loaded.WindowWidthDip);
        Assert.Equal(604.0, loaded.WindowHeightDip);
    }

    [Fact]
    public void Load_quarantines_corrupt_json_and_returns_null()
    {
        var path = Path.Combine(_storage, "window-placement.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(_store.Load());
        Assert.False(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(_storage, "window-placement.json.corrupt.*"));
    }

    [Fact]
    public void A_leftover_temp_file_preserves_the_previous_placement()
    {
        _store.Save(new WindowPlacement("\\\\.\\DISPLAY2", 10.0, 20.0, 1.5, 1.5, 300.0, 600.0));
        File.WriteAllText(Path.Combine(_storage, "window-placement.json.tmp"), "{ garbage from an interrupted write");

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("\\\\.\\DISPLAY2", loaded!.MonitorId);
    }
}