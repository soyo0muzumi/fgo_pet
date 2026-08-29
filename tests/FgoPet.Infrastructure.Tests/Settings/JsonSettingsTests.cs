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

    public static TheoryData<string> MalformedPackageSettings => new()
    {
        { "{\"mash_kyrielight\":null}" },
        { "{\"\":{\"show_status\":\"true\"}}" },
        { "{\"invalid servant\":{\"show_status\":\"true\"}}" },
        { $"{{\"{new string('s', 129)}\":{{\"show_status\":\"true\"}}}}" },
        { "{\"mash_kyrielight\":{\"\":\"true\"}}" },
        { "{\"mash_kyrielight\":{\"Invalid key\":\"true\"}}" },
        { $"{{\"mash_kyrielight\":{{\"{new string('s', 65)}\":\"true\"}}}}" },
        { "{\"mash_kyrielight\":{\"show_status\":null}}" },
        { $"{{\"mash_kyrielight\":{{\"greeting\":\"{new string('x', 257)}\"}}}}" },
    };

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
        Assert.Equal(AppTheme.ModernGray, settings.Theme);
        Assert.Null(settings.UserProfile);
        Assert.Empty(settings.PackageSettings);
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
            Theme = AppTheme.FgoLight,
            UserProfile = new UserProfile("xqj"),
            PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["mash_kyrielight"] = new Dictionary<string, string> { ["show_status"] = "true" },
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
        Assert.Equal(AppTheme.FgoLight, loaded.Theme);
        Assert.Equal("xqj", loaded.UserProfile!.DisplayName);
        Assert.Equal("true", loaded.PackageSettings["mash_kyrielight"]["show_status"]);

        var json = File.ReadAllText(_store.Location);
        Assert.Contains("\"theme\":\"fgo_light\"", json);
        Assert.Contains("\"user_profile\"", json);
        Assert.Contains("\"display_name\":\"xqj\"", json);
        Assert.Contains("\"package_settings\"", json);
    }

    [Fact]
    public void Load_uses_modern_gray_for_an_unknown_theme_without_quarantining_settings()
    {
        File.WriteAllText(Path.Combine(_storage, "settings.json"), """
            {
              "schema_version": 2,
              "selection": null,
              "scale": 0.5,
              "topmost": true,
              "auto_collapse": true,
              "theme": "unknown_theme"
            }
            """);

        var loaded = _store.Load();

        Assert.Equal(AppTheme.ModernGray, loaded.Theme);
        Assert.Empty(Directory.GetFiles(_storage, "settings.json.corrupt.*"));
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

    [Theory]
    [MemberData(nameof(MalformedPackageSettings))]
    public void Load_quarantines_malformed_package_settings_and_returns_defaults(string packageSettings)
    {
        var path = Path.Combine(_storage, "settings.json");
        File.WriteAllText(path, $$"""
            {
              "schema_version": 2,
              "scale": 0.75,
              "topmost": false,
              "auto_collapse": false,
              "package_settings": {{packageSettings}}
            }
            """);

        var settings = _store.Load();

        Assert.Equal(AppSettings.Defaults, settings);
        Assert.False(File.Exists(path));
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
