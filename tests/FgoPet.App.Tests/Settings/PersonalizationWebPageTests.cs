using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using FgoPet.App.Settings;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class PersonalizationWebPageTests
{
    [Fact]
    public async Task Constructor_is_lazy_and_commands_save_each_supported_value_while_preserving_other_fields()
    {
        var savedProfile = new UserProfile("名字");
        var preferences = new Dictionary<string, ServantPreference>
        {
            ["servant"] = new(AddressMode.UserDefined, "御主"),
        };
        var packageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["package"] = new Dictionary<string, string> { ["color"] = "violet" },
        };
        var store = new CharacterStore(CharacterSettings.Defaults with
        {
            Scale = 0.75,
            UserProfile = savedProfile,
            ServantPreferences = preferences,
            PackageSettings = packageSettings,
        });
        var portrait = new PortraitController();
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var page = new PersonalizationWebPage(store, portrait, theme);

        Assert.Equal(0, store.LoadCount);
        Assert.NotSame(page, page.CreateSession());

        var scale = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"scale\",\"value\":0.6}");
        var topmost = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"topmost\",\"value\":false}");
        var collapse = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"autoCollapseExpandedPanel\",\"value\":false}");

        Assert.True(scale.Success);
        Assert.True(topmost.Success);
        Assert.True(collapse.Success);
        Assert.Equal(0.6, store.Value.Scale);
        Assert.False(store.Value.Topmost);
        Assert.False(store.Value.AutoCollapseExpandedPanel);
        Assert.Equal(0.6, Assert.Single(portrait.Scales));
        Assert.Equal(3, store.SaveCount);
        Assert.Same(savedProfile, store.Value.UserProfile);
        Assert.Same(preferences, store.Value.ServantPreferences);
        Assert.Same(packageSettings, store.Value.PackageSettings);

        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(collapse.Payload));
        Assert.Equal(0.6, snapshot.RootElement.GetProperty("scale").GetDouble());
        Assert.Equal(new[] { 0.5, 0.6, 0.75 }, snapshot.RootElement.GetProperty("scaleOptions").EnumerateArray().Select(item => item.GetDouble()));
        Assert.Equal("", snapshot.RootElement.GetProperty("errorText").GetString());
    }

    [Fact]
    public async Task Invalid_commands_do_not_write_or_apply_and_failed_save_can_retry_same_value()
    {
        var store = new CharacterStore(CharacterSettings.Defaults) { FailNextSave = true };
        var portrait = new PortraitController();
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var page = new PersonalizationWebPage(store, portrait, theme);

        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "personalization.set", "null")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"scale\",\"value\":0.8}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"topmost\",\"value\":1}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"other\",\"value\":true}")).ErrorCode);
        Assert.Equal(0, store.LoadCount);

        var failed = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"topmost\",\"value\":false}");
        Assert.False(failed.Success);
        Assert.Equal("SETTINGS_SAVE_FAILED", failed.ErrorCode);
        Assert.True(store.Value.Topmost);
        using (var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(failed.Payload)))
        {
            var persisted = snapshot.RootElement.GetProperty("snapshot");
            Assert.True(persisted.TryGetProperty("scale", out _));
            Assert.True(persisted.GetProperty("topmost").GetBoolean());
            Assert.Contains("保存失败", persisted.GetProperty("errorText").GetString());
        }

        var retried = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"topmost\",\"value\":false}");
        Assert.True(retried.Success);
        Assert.False(store.Value.Topmost);
        Assert.Equal(2, store.SaveCount);
        Assert.Empty(portrait.Scales);
    }

    [Fact]
    public async Task Failed_store_read_is_unavailable_and_later_get_recovers_without_writes()
    {
        var store = new CharacterStore(CharacterSettings.Defaults) { FailNextLoad = true };
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var page = new PersonalizationWebPage(store, null, theme);

        var failed = await Send(page, "personalization.get", "{\"pageId\":\"Personalization\"}");
        Assert.False(failed.Success);
        Assert.Equal("SETTINGS_UNAVAILABLE", failed.ErrorCode);
        var recovered = await Send(page, "personalization.get", "{\"pageId\":\"Personalization\"}");
        Assert.True(recovered.Success);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Unsupported_persisted_scale_uses_the_legacy_display_fallback_and_can_be_reset()
    {
        var store = new CharacterStore(CharacterSettings.Defaults with { Scale = 0.82 });
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var page = new PersonalizationWebPage(store, null, theme);

        var read = await Send(page, "personalization.get", "{\"pageId\":\"Personalization\"}");
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(read.Payload));
        Assert.Equal(CharacterSettings.Defaults.Scale, snapshot.RootElement.GetProperty("scale").GetDouble());
        Assert.Equal(0.82, store.Value.Scale);

        var reset = await Send(page, "personalization.reset", "{\"pageId\":\"Personalization\"}");
        Assert.True(reset.Success);
        Assert.Equal(CharacterSettings.Defaults.Scale, store.Value.Scale);
    }

    [Fact]
    public async Task Personalization_sessions_serialize_sync_writes_so_concurrent_fields_are_not_lost()
    {
        var store = new FirstLoadGateStore(CharacterSettings.Defaults);
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var first = new PersonalizationWebPage(store, null, theme);
        var second = Assert.IsType<PersonalizationWebPage>(first.CreateSession());

        var firstWrite = Task.Run(() => Send(first, "personalization.set",
            "{\"pageId\":\"Personalization\",\"field\":\"topmost\",\"value\":false}"));
        Assert.True(store.FirstLoadEntered.Wait(TimeSpan.FromSeconds(5)));
        var secondWrite = Task.Run(() => Send(second, "personalization.set",
            "{\"pageId\":\"Personalization\",\"field\":\"autoCollapseExpandedPanel\",\"value\":false}"));
        var concurrentRead = store.SubsequentLoadEntered.Wait(TimeSpan.FromMilliseconds(150));

        try
        {
            await Task.Delay(100);
        }
        finally
        {
            store.ReleaseFirstLoad.Set();
        }

        Assert.False(concurrentRead);
        Assert.True((await firstWrite).Success);
        Assert.True((await secondWrite).Success);
        Assert.False(store.Value.Topmost);
        Assert.False(store.Value.AutoCollapseExpandedPanel);
    }

    [Fact]
    public async Task Scale_change_without_an_active_portrait_keeps_the_existing_activation_status()
    {
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var page = new PersonalizationWebPage(new CharacterStore(CharacterSettings.Defaults),
            new PortraitController { NoActivePortrait = true }, theme);

        var result = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"scale\",\"value\":0.6}");

        Assert.True(result.Success);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        Assert.Contains("激活角色后生效", snapshot.RootElement.GetProperty("statusText").GetString());
    }

    [Fact]
    public async Task Failed_scale_save_with_no_active_portrait_keeps_the_failure_status()
    {
        var store = new CharacterStore(CharacterSettings.Defaults) { FailNextSave = true };
        using var theme = CreateTheme(new ThemeStore(new ThemeSettings(AppTheme.FgoLight)));
        var page = new PersonalizationWebPage(store, new PortraitController { NoActivePortrait = true }, theme);

        var result = await Send(page, "personalization.set", "{\"pageId\":\"Personalization\",\"field\":\"scale\",\"value\":0.6}");

        Assert.False(result.Success);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        var snapshot = payload.RootElement.GetProperty("snapshot");
        Assert.Contains("保存失败", snapshot.GetProperty("statusText").GetString());
        Assert.DoesNotContain("已保存", snapshot.GetProperty("statusText").GetString());
        Assert.Contains("保存失败", snapshot.GetProperty("errorText").GetString());
    }

    [Fact]
    public async Task Reset_changes_only_the_three_personalization_fields_and_keeps_theme_selection()
    {
        var profile = new UserProfile("保持名称");
        var preferences = new Dictionary<string, ServantPreference> { ["servant"] = new(AddressMode.PackageDefault) };
        var packageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["package"] = new Dictionary<string, string> { ["x"] = "y" } };
        var store = new CharacterStore(CharacterSettings.Defaults with
        {
            Scale = 0.75, Topmost = false, AutoCollapseExpandedPanel = false,
            UserProfile = profile, ServantPreferences = preferences, PackageSettings = packageSettings,
        });
        var themeStore = new ThemeStore(new ThemeSettings(AppTheme.FgoDark));
        using var theme = CreateTheme(themeStore);
        var page = new PersonalizationWebPage(store, null, theme);
        var result = await Send(page, "personalization.reset", "{\"pageId\":\"Personalization\"}");

        Assert.True(result.Success);
        Assert.Equal(CharacterSettings.Defaults.Scale, store.Value.Scale);
        Assert.Equal(CharacterSettings.Defaults.Topmost, store.Value.Topmost);
        Assert.Equal(CharacterSettings.Defaults.AutoCollapseExpandedPanel, store.Value.AutoCollapseExpandedPanel);
        Assert.Same(profile, store.Value.UserProfile);
        Assert.Same(preferences, store.Value.ServantPreferences);
        Assert.Same(packageSettings, store.Value.PackageSettings);
        Assert.Equal(AppTheme.FgoDark, themeStore.Value.Theme);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Theme_commands_report_theme_service_selected_effective_and_status_for_all_modes()
    {
        var themeStore = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
        using var theme = CreateTheme(themeStore, systemTheme: AppTheme.FgoDark);
        var page = new PersonalizationWebPage(new CharacterStore(CharacterSettings.Defaults), null, theme);

        foreach (var (name, selected, effective) in new[]
        {
            ("System", "System", "FgoDark"),
            ("FgoLight", "FgoLight", "FgoLight"),
            ("FgoDark", "FgoDark", "FgoDark"),
        })
        {
            var result = await Send(page, "personalization.setTheme", $"{{\"pageId\":\"Personalization\",\"theme\":\"{name}\"}}");
            Assert.True(result.Success);
            using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
            var actual = snapshot.RootElement.GetProperty("theme");
            Assert.Equal(selected, actual.GetProperty("selected").GetString());
            Assert.Equal(effective, actual.GetProperty("effective").GetString());
            Assert.False(string.IsNullOrWhiteSpace(actual.GetProperty("statusText").GetString()));
            Assert.Equal(name, themeStore.Value.Theme == AppTheme.System ? "System" : themeStore.Value.Theme.ToString());
        }
    }

    [Fact]
    public async Task Theme_apply_failure_returns_the_theme_service_actual_selection_and_status()
    {
        var themeStore = new ThemeStore(new ThemeSettings(AppTheme.FgoLight));
        using var theme = CreateTheme(themeStore, failApply: AppTheme.FgoDark);
        var page = new PersonalizationWebPage(new CharacterStore(CharacterSettings.Defaults), null, theme);

        var result = await Send(page, "personalization.setTheme", "{\"pageId\":\"Personalization\",\"theme\":\"FgoDark\"}");

        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        var actual = snapshot.RootElement.GetProperty("theme");
        Assert.Equal("FgoLight", actual.GetProperty("selected").GetString());
        Assert.Equal("FgoLight", actual.GetProperty("effective").GetString());
        Assert.Contains("保留当前主题", actual.GetProperty("statusText").GetString());
        Assert.Equal(AppTheme.FgoLight, themeStore.Value.Theme);
    }

    [Fact]
    public async Task Theme_persist_failure_keeps_the_theme_service_actual_state_and_status()
    {
        var themeStore = new ThemeStore(new ThemeSettings(AppTheme.FgoLight)) { FailNextSave = true };
        using var theme = CreateTheme(themeStore);
        var page = new PersonalizationWebPage(new CharacterStore(CharacterSettings.Defaults), null, theme);

        var result = await Send(page, "personalization.setTheme", "{\"pageId\":\"Personalization\",\"theme\":\"FgoDark\"}");

        Assert.True(result.Success);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        var actual = snapshot.RootElement.GetProperty("theme");
        Assert.Equal("FgoDark", actual.GetProperty("selected").GetString());
        Assert.Equal("FgoDark", actual.GetProperty("effective").GetString());
        Assert.Contains("保存失败", actual.GetProperty("statusText").GetString());
        Assert.Equal(AppTheme.FgoLight, themeStore.Value.Theme);
    }

    [Fact]
    public void Preview_export_rejects_oversized_local_files_without_returning_a_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "fgopet-preview-bound-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.SetLength(12 * 1024 * 1024 + 1);

            Assert.Null(PersonalizationWebPage.TryCreatePreviewDataUrl(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Preview_export_returns_null_for_unsupported_image_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "fgopet-preview-invalid-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(path, [0x46, 0x47, 0x4f, 0x2d, 0x50, 0x45, 0x54]);

            Assert.Null(PersonalizationWebPage.TryCreatePreviewDataUrl(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task<WebSurfaceCommandResult> Send(PersonalizationWebPage page, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return await page.HandleCommandAsync(new WebSurfaceMessage(command, null, document.RootElement.Clone()), CancellationToken.None);
    }

    private static ThemeService CreateTheme(ThemeStore store, AppTheme systemTheme = AppTheme.FgoLight,
        AppTheme? failApply = null)
    {
        var service = new ThemeService(store, new ResourceDictionary(), theme =>
        {
            if (theme == failApply) throw new InvalidOperationException("test resource failure");
            return new ResourceDictionary();
        }, new SystemTheme(systemTheme));
        service.Initialize();
        return service;
    }

    private sealed class CharacterStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        public CharacterSettings Value { get; private set; } = initial;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public bool FailNextLoad { get; set; }
        public bool FailNextSave { get; set; }

        public CharacterSettings Load()
        {
            LoadCount++;
            if (FailNextLoad)
            {
                FailNextLoad = false;
                throw new IOException("synthetic read failure");
            }
            return Value;
        }

        public void Save(CharacterSettings settings)
        {
            SaveCount++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("synthetic write failure");
            }
            Value = settings;
        }
    }

    private sealed class FirstLoadGateStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        private readonly object _gate = new();
        private CharacterSettings _value = initial;
        private int _loadCount;

        public ManualResetEventSlim FirstLoadEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseFirstLoad { get; } = new(false);
        public ManualResetEventSlim SubsequentLoadEntered { get; } = new(false);
        public CharacterSettings Value { get { lock (_gate) return _value; } }

        public CharacterSettings Load()
        {
            CharacterSettings snapshot;
            bool first;
            lock (_gate)
            {
                first = ++_loadCount == 1;
                snapshot = _value;
            }
            if (first)
            {
                FirstLoadEntered.Set();
                ReleaseFirstLoad.Wait(TimeSpan.FromSeconds(5));
            }
            else
            {
                SubsequentLoadEntered.Set();
            }
            return snapshot;
        }

        public void Save(CharacterSettings settings)
        {
            lock (_gate) _value = settings;
        }
    }

    private sealed class ThemeStore(ThemeSettings initial) : IThemeSettingsStore
    {
        public ThemeSettings Value { get; private set; } = initial;
        public bool FailNextSave { get; set; }
        public ThemeSettings Load() => Value;
        public void Save(ThemeSettings settings)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("synthetic theme write failure");
            }
            Value = settings;
        }
    }

    private sealed class SystemTheme(AppTheme current) : ISystemThemeSource
    {
        public event EventHandler? Changed { add { } remove { } }
        public AppTheme ReadTheme() => current;
    }

    private sealed class PortraitController : IPortraitController
    {
        public List<double> Scales { get; } = [];
        public bool NoActivePortrait { get; init; }
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale)
        {
            if (NoActivePortrait)
                throw new InvalidOperationException("No active portrait.");
            Scales.Add(scale);
        }
        public void ApplyDpi(Dpi2 dpi) { }
    }
}
