using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using FgoPet.App.Theming;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.Windows.Tests.Theming;

[Trait("Category", "WindowsIntegration")]
public sealed class ThemeServiceTests
{
    [Fact]
    public void Initialize_applies_the_saved_theme_and_preserves_unrelated_resources()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var unrelated = new ResourceDictionary { ["UnrelatedResource"] = "keep" };
            resources.MergedDictionaries.Add(unrelated);
            var store = new MemorySettingsStore(AppSettings.Defaults with { Theme = AppTheme.FgoLight });
            var service = new ThemeService(store, resources);

            service.Initialize();

            Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
            Assert.Equal("keep", resources["UnrelatedResource"]);
            Assert.Single(resources.MergedDictionaries.Where(ThemeService.IsMarkedThemeDictionary));
            Assert.Equal(AppTheme.FgoLight, ThemeService.GetMarkedTheme(resources.MergedDictionaries.Single(ThemeService.IsMarkedThemeDictionary)));
        });
    }

    [Fact]
    public void Select_applies_immediately_persists_and_notifies_once()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var store = new MemorySettingsStore(AppSettings.Defaults);
            var service = new ThemeService(store, resources);
            service.Initialize();
            var notifications = 0;
            service.ThemeChanged += (_, _) => notifications++;

            service.Select(AppTheme.FgoLight);

            Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
            Assert.Equal(AppTheme.FgoLight, store.Load().Theme);
            Assert.Equal(1, notifications);
            Assert.Contains("FGO", service.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Select_keeps_the_previous_theme_when_the_next_dictionary_cannot_load()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var store = new MemorySettingsStore(AppSettings.Defaults);
            var service = new ThemeService(
                store,
                resources,
                theme => theme == AppTheme.FgoLight
                    ? AssertPreviousThemeThenThrow(resources)
                    : ThemeService.CreateTestDictionary(theme));
            service.Initialize();
            var notifications = 0;
            service.ThemeChanged += (_, _) => notifications++;

            service.Select(AppTheme.FgoLight);

            Assert.Equal(AppTheme.ModernGray, service.CurrentTheme);
            Assert.Equal(AppTheme.ModernGray, store.Load().Theme);
            Assert.Equal(0, notifications);
            Assert.Contains("加载失败", service.StatusText, StringComparison.Ordinal);
            Assert.Single(resources.MergedDictionaries.Where(ThemeService.IsMarkedThemeDictionary));
        });
    }

    [Fact]
    public void Select_keeps_the_applied_theme_when_persistence_fails()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var store = new ThrowingSaveSettingsStore(AppSettings.Defaults);
            var service = new ThemeService(store, resources);
            service.Initialize();
            var notifications = 0;
            service.ThemeChanged += (_, _) => notifications++;

            service.Select(AppTheme.FgoLight);

            Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
            Assert.Equal(AppTheme.FgoLight, ThemeService.GetMarkedTheme(resources.MergedDictionaries.Single(ThemeService.IsMarkedThemeDictionary)));
            Assert.Contains("保存失败", service.StatusText, StringComparison.Ordinal);
            Assert.Equal(1, notifications);
        });
    }

    [Fact]
    public void Initialize_uses_a_complete_modern_gray_fallback_when_the_initial_dictionary_cannot_load()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var service = new ThemeService(
                new MemorySettingsStore(AppSettings.Defaults with { Theme = AppTheme.FgoLight }),
                resources,
                _ => throw new IOException("test resource failure"));

            service.Initialize();

            Assert.Equal(AppTheme.ModernGray, service.CurrentTheme);
            var fallback = Assert.Single(resources.MergedDictionaries.Where(ThemeService.IsMarkedThemeDictionary));
            foreach (var key in RequiredStyleBrushKeys)
            {
                Assert.IsAssignableFrom<Brush>(fallback[key]);
            }
            Assert.Contains("回退", service.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Shared_resources_expose_matching_palettes_controls_and_application_owned_icons()
    {
        StaRun(() =>
        {
            var modern = LoadDictionary("ModernGray.xaml");
            var light = LoadDictionary("FgoLight.xaml");
            var controls = LoadDictionary("SettingsControls.xaml");
            var icons = LoadDictionary("SettingsIcons.xaml");

            Assert.Equal(
                modern.Keys.Cast<object>().Select(key => key.ToString()).OrderBy(key => key),
                light.Keys.Cast<object>().Select(key => key.ToString()).OrderBy(key => key));
            foreach (var key in RequiredStyleBrushKeys)
            {
                Assert.IsAssignableFrom<Brush>(modern[key]);
                Assert.IsAssignableFrom<Brush>(light[key]);
            }
            foreach (var key in RequiredControlStyleKeys)
            {
                Assert.IsType<Style>(controls[key]);
            }
            foreach (var key in RequiredIconKeys)
            {
                Assert.IsAssignableFrom<Geometry>(icons[key]);
            }
        });
    }

    private static readonly string[] RequiredStyleBrushKeys =
    [
        "AccentBrush",
        "AccentSoftBrush",
        "BorderBrush",
        "BorderStrongBrush",
        "DangerBrush",
        "DangerSoftBrush",
        "FocusBrush",
        "InputBackgroundBrush",
        "MutedTextBrush",
        "SidebarForegroundBrush",
        "SidebarHoverBrush",
        "SidebarSelectedBrush",
        "SidebarSelectedTextBrush",
        "SurfaceBrush",
        "SurfaceRaisedBrush",
        "TextBrush",
        "TextOnAccentBrush",
        "WarningBrush",
        "WindowBackgroundBrush",
    ];

    private static readonly string[] RequiredControlStyleKeys =
    [
        "SettingsNavigationItemStyle",
        "SettingsCardStyle",
        "SettingsPrimaryButtonStyle",
        "SettingsSecondaryButtonStyle",
        "SettingsDangerButtonStyle",
        "SettingsTextBoxStyle",
        "SettingsComboBoxStyle",
        "SettingsPageHeaderStyle",
        "SettingsCaptionStyle",
        "SettingsStatusStyle",
        "SettingsThemeChoiceStyle",
    ];

    private static readonly string[] RequiredIconKeys =
    [
        "IconProfileGeometry",
        "IconPersonalizationGeometry",
        "IconRolePackageGeometry",
        "IconConnectionGeometry",
        "IconConversationGeometry",
        "IconPrivacyGeometry",
        "IconThemeGeometry",
        "IconAppearanceGeometry",
        "IconAddressGeometry",
        "IconPackageInfoGeometry",
    ];

    private static ResourceDictionary LoadDictionary(string fileName) =>
        new()
        {
            Source = new Uri($"/FgoPet.App;component/Themes/{fileName}", UriKind.Relative),
        };

    private static ResourceDictionary AssertPreviousThemeThenThrow(ResourceDictionary resources)
    {
        Assert.Single(resources.MergedDictionaries.Where(ThemeService.IsMarkedThemeDictionary));
        throw new InvalidOperationException("test load failure");
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class MemorySettingsStore(AppSettings settings) : IAppSettingsStore
    {
        private AppSettings _settings = settings;

        public string Location => "memory";

        public AppSettings Load() => _settings;

        public void Save(AppSettings settings) => _settings = settings;
    }

    private sealed class ThrowingSaveSettingsStore(AppSettings settings) : IAppSettingsStore
    {
        public string Location => "memory";

        public AppSettings Load() => settings;

        public void Save(AppSettings settings) => throw new IOException("test save failure");
    }
}
