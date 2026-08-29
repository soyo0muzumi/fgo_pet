using System.IO;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Settings;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Settings;

public sealed class JsonSettingsTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "fgo-pet-settings-" + Guid.NewGuid().ToString("N"));
    private readonly JsonAppSettingsStore _store;

    public JsonSettingsTests()
    {
        Directory.CreateDirectory(_storage);
        _store = new JsonAppSettingsStore(_storage);
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
    public void Load_returns_defaults_when_missing()
    {
        var settings = _store.Load();
        Assert.Null(settings.Selection);
        Assert.Equal(0.50, settings.Scale);
        Assert.True(settings.Topmost);
        Assert.True(settings.AutoCollapseExpandedPanel);
        Assert.Null(settings.ModelConnection);
        Assert.True(settings.MemoryEnabled);
        Assert.Empty(settings.ServantPreferences);
    }

    [Fact]
    public void Save_then_Load_roundtrips_every_field()
    {
        var saved = new AppSettings(
            Selection: new PortraitSelection("official.mash", "casual", "1.0.0"),
            Scale: 0.75,
            Topmost: false,
            AutoCollapseExpandedPanel: false);

        saved = saved with
        {
            ModelConnection = new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-chat"),
            MemoryEnabled = false,
            ServantPreferences = new Dictionary<string, ServantPreference>
            {
                ["800100"] = new ServantPreference(AddressMode.UserDefined, "御主"),
            },
        };

        _store.Save(saved);

        var loaded = _store.Load();
        Assert.Equal(saved.Selection, loaded.Selection);
        Assert.Equal(0.75, loaded.Scale);
        Assert.False(loaded.Topmost);
        Assert.False(loaded.AutoCollapseExpandedPanel);
        Assert.Equal(saved.ModelConnection, loaded.ModelConnection);
        Assert.False(loaded.MemoryEnabled);
        Assert.Equal(saved.ServantPreferences, loaded.ServantPreferences);
    }

    [Fact]
    public void Save_does_not_write_an_api_key_property()
    {
        _store.Save(AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
        });

        var json = File.ReadAllText(_store.Location);

        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_quarantines_corrupt_json_and_returns_defaults()
    {
        var path = Path.Combine(_storage, "settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = _store.Load();

        Assert.Equal(AppSettings.Defaults, settings);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(_storage, "settings.json.corrupt.*"));
    }

    [Fact]
    public void Load_quarantines_an_unsupported_schema()
    {
        var path = Path.Combine(_storage, "settings.json");
        File.WriteAllText(path, "{\"schema_version\": 99, \"scale\": 0.75}");

        var settings = _store.Load();

        Assert.Equal(AppSettings.Defaults, settings);
        Assert.NotEmpty(Directory.GetFiles(_storage, "settings.json.corrupt.*"));
    }

    [Fact]
    public void A_leftover_temp_file_preserves_the_previous_valid_settings()
    {
        var saved = new AppSettings(new PortraitSelection("official.mash", "casual"), 0.75, false, false);
        _store.Save(saved);
        File.WriteAllText(Path.Combine(_storage, "settings.json.tmp"), "{ garbage from an interrupted write");

        var loaded = _store.Load();

        Assert.Equal(saved, loaded);
        Assert.Empty(Directory.GetFiles(_storage, "settings.json.corrupt.*"));
    }
}
