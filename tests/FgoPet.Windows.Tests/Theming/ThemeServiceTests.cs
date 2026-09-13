using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
            var store = new MemorySettingsStore(AppSettings.Defaults with { Theme = AppTheme.ModernGray });
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
    public void Initialize_uses_a_complete_light_fallback_when_the_initial_dictionary_cannot_load()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var service = new ThemeService(
                new MemorySettingsStore(AppSettings.Defaults with { Theme = AppTheme.FgoLight }),
                resources,
                _ => throw new IOException("test resource failure"));

            service.Initialize();

            Assert.Equal(AppTheme.FgoLight, service.CurrentTheme);
            var fallback = Assert.Single(resources.MergedDictionaries.Where(ThemeService.IsMarkedThemeDictionary));
            foreach (var key in RequiredStyleBrushKeys)
            {
                Assert.IsAssignableFrom<Brush>(fallback[key]);
            }
            AssertColor(fallback, "Semantic.AppColor", "#FFF7F6FB");
            AssertColor(fallback, "Semantic.ContentColor", "#FFFFFFFF");
            AssertColor(fallback, "Semantic.SubtleColor", "#FFEEE8FA");
            AssertColor(fallback, "Semantic.PrimaryTextColor", "#FF252137");
            AssertColor(fallback, "Semantic.SecondaryTextColor", "#FF6D687B");
            AssertColor(fallback, "Semantic.PrimaryActionColor", "#FF7050B8");
            AssertColor(fallback, "Semantic.OnPrimaryActionColor", "#FFFFFFFF");
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

    [Theory]
    [InlineData("FgoLight.xaml", "#FFF7F6FB", "#FFFFFFFF", "#FFEEE8FA", "#FF252137", "#FF6D687B", "#FF827A70", "#FF7050B8", "#FFFFFFFF")]
    [InlineData("ModernGray.xaml", "#FF16131D", "#FF282332", "#FF3B3150", "#FFFAF8FF", "#FFCBC5D7", "#FF5A5069", "#FFC3A8FF", "#FF1A1324")]
    public void Theme_palettes_resolve_approved_semantics_compatibility_mappings_and_contrast(
        string fileName,
        string app,
        string content,
        string subtle,
        string primaryText,
        string secondaryText,
        string controlBorder,
        string primaryAction,
        string onPrimaryAction)
    {
        StaRun(() =>
        {
            var theme = LoadDictionary(fileName);

            AssertColor(theme, "Semantic.AppColor", app);
            AssertColor(theme, "Semantic.ContentColor", content);
            AssertColor(theme, "Semantic.SubtleColor", subtle);
            AssertColor(theme, "Semantic.PrimaryTextColor", primaryText);
            AssertColor(theme, "Semantic.SecondaryTextColor", secondaryText);
            AssertColor(theme, "Semantic.BorderControlColor", controlBorder);
            AssertColor(theme, "Semantic.PrimaryActionColor", primaryAction);
            AssertColor(theme, "Semantic.OnPrimaryActionColor", onPrimaryAction);

            AssertCompatibilityColor(theme, "Semantic.AppColor", "WindowBackgroundColor", "ShellWindowColor");
            AssertCompatibilityColor(theme, "Semantic.ContentColor", "SurfaceColor", "ShellRaisedColor");
            AssertCompatibilityColor(theme, "Semantic.SubtleColor", "SurfaceRaisedColor", "SidebarSelectedColor", "ShellPanelColor");
            AssertCompatibilityColor(theme, "Semantic.PrimaryTextColor", "TextColor", "SidebarSelectedTextColor", "ShellTextColor");
            AssertCompatibilityColor(theme, "Semantic.SecondaryTextColor", "MutedTextColor", "SidebarForegroundColor", "ShellMutedColor");
            AssertCompatibilityColor(theme, "Semantic.PrimaryActionColor", "AccentColor", "ShellAccentColor");
            AssertCompatibilityColor(theme, "Semantic.OnPrimaryActionColor", "TextOnAccentColor");
            AssertCompatibilityColor(theme, "Semantic.BorderControlColor", "BorderStrongColor", "ShellLineColor");

            Assert.True(ContrastRatio(ColorOf(theme, "Semantic.PrimaryTextColor"), ColorOf(theme, "Semantic.AppColor")) >= 4.5);
            Assert.True(ContrastRatio(ColorOf(theme, "Semantic.SecondaryTextColor"), ColorOf(theme, "Semantic.AppColor")) >= 4.5);
            Assert.True(ContrastRatio(ColorOf(theme, "Semantic.PrimaryTextColor"), ColorOf(theme, "Semantic.ContentColor")) >= 4.5);
            Assert.True(ContrastRatio(ColorOf(theme, "Semantic.SecondaryTextColor"), ColorOf(theme, "Semantic.ContentColor")) >= 4.5);
            Assert.True(ContrastRatio(ColorOf(theme, "Semantic.OnPrimaryActionColor"), ColorOf(theme, "Semantic.PrimaryActionColor")) >= 4.5);
        });
    }

    [Theory]
    [InlineData("FgoLight.xaml", "#FFF7F6FB", "#FFFFFFFF", "#FF252137", "#FF6D687B", "#FF7050B8")]
    [InlineData("ModernGray.xaml", "#FF16131D", "#FF282332", "#FFFAF8FF", "#FFCBC5D7", "#FFC3A8FF")]
    public void Ui_foundation_and_shell_brushes_follow_the_loaded_theme_dictionary(
        string fileName,
        string app,
        string content,
        string primaryText,
        string secondaryText,
        string primaryAction)
    {
        StaRun(() =>
        {
            var host = new Border();
            host.Resources.MergedDictionaries.Add(LoadComponentDictionary("UiFoundation/ThemeTokens.xaml"));
            host.Resources.MergedDictionaries.Add(LoadDictionary(fileName));
            host.Resources.MergedDictionaries.Add(LoadComponentDictionary("Ui/Shell/ShellTokens.xaml"));

            Assert.Equal(app, ResolvedBrushColor(host, "Surface.App").ToString());
            Assert.Equal(app, ResolvedBrushColor(host, "ShellWindowBackgroundBrush").ToString());
            Assert.Equal(content, ResolvedBrushColor(host, "Surface.Content").ToString());
            Assert.Equal(content, ResolvedBrushColor(host, "ShellRaisedBackgroundBrush").ToString());
            Assert.Equal(primaryText, ResolvedBrushColor(host, "Text.Primary").ToString());
            Assert.Equal(primaryText, ResolvedBrushColor(host, "ShellTextBrush").ToString());
            Assert.Equal(secondaryText, ResolvedBrushColor(host, "Text.Secondary").ToString());
            Assert.Equal(secondaryText, ResolvedBrushColor(host, "ShellMutedTextBrush").ToString());
            Assert.Equal(primaryAction, ResolvedBrushColor(host, "Action.Primary").ToString());
            Assert.Equal(primaryAction, ResolvedBrushColor(host, "ShellAccentBrush").ToString());
        });
    }

    [Fact]
    public void Danger_button_template_keeps_hover_and_pressed_interactions_danger_coded()
    {
        StaRun(() =>
        {
            var controls = LoadDictionary("SettingsControls.xaml");
            var dangerStyle = Assert.IsType<Style>(controls["SettingsDangerButtonStyle"]);
            var dangerTemplate = Assert.IsType<ControlTemplate>(FindSetter(dangerStyle, Control.TemplateProperty).Value);

            Assert.NotSame(controls["SettingsButtonTemplate"], dangerTemplate);

            var hover = FindTrigger(dangerTemplate, UIElement.IsMouseOverProperty, true);
            Assert.Equal("DangerSoftBrush", DynamicResourceKey(FindTriggerSetter(hover, Border.BackgroundProperty).Value));
            Assert.Equal("DangerHoverBrush", DynamicResourceKey(FindTriggerSetter(hover, Border.BorderBrushProperty).Value));

            var pressed = FindTrigger(dangerTemplate, ButtonBase.IsPressedProperty, true);
            Assert.Equal("DangerBrush", DynamicResourceKey(FindTriggerSetter(pressed, Border.BackgroundProperty).Value));
            Assert.Equal("DangerHoverBrush", DynamicResourceKey(FindTriggerSetter(pressed, Border.BorderBrushProperty).Value));
            Assert.Equal("TextOnAccentBrush", DynamicResourceKey(FindTriggerSetter(pressed, TextElement.ForegroundProperty).Value));
        });
    }

    [Fact]
    public void Theme_choice_style_exposes_a_visible_keyboard_focus_contract()
    {
        StaRun(() =>
        {
            var controls = LoadDictionary("SettingsControls.xaml");
            var themeChoice = Assert.IsType<Style>(controls["SettingsThemeChoiceStyle"]);
            var focusStyle = Assert.IsType<Style>(FindSetter(themeChoice, Control.FocusVisualStyleProperty).Value);
            var focusTemplate = Assert.IsType<ControlTemplate>(FindSetter(focusStyle, Control.TemplateProperty).Value);
            var focusControl = new Control
            {
                Template = focusTemplate,
            };
            focusControl.Resources.MergedDictionaries.Add(LoadDictionary("ModernGray.xaml"));
            Assert.True(focusControl.ApplyTemplate());
            var focusBorder = Assert.IsType<Border>(VisualTreeHelper.GetChild(focusControl, 0));

            Assert.Equal("#FFD5C2FF", ((SolidColorBrush)focusBorder.BorderBrush).Color.ToString());
            Assert.Equal(new Thickness(2), focusBorder.BorderThickness);
        });
    }

    [Fact]
    public void Shared_form_controls_replace_native_white_surfaces_and_scrollbars()
    {
        StaRun(() =>
        {
            var controls = LoadDictionary("SettingsControls.xaml");

            foreach (var key in new[]
                     {
                         "SettingsPasswordBoxStyle",
                         "SettingsListBoxStyle",
                         "SettingsScrollBarStyle",
                     })
            {
                Assert.IsType<Style>(controls[key]);
            }

            var listStyle = Assert.IsType<Style>(controls["SettingsListBoxStyle"]);
            Assert.Equal("InputBackgroundBrush", DynamicResourceKey(FindSetter(listStyle, Control.BackgroundProperty).Value));

            var comboStyle = Assert.IsType<Style>(controls["SettingsComboBoxStyle"]);
            Assert.IsType<ControlTemplate>(FindSetter(comboStyle, Control.TemplateProperty).Value);
        });
    }

    [Fact]
    public void Theme_choice_is_a_card_with_checked_state_instead_of_a_stretched_native_radio()
    {
        StaRun(() =>
        {
            var controls = LoadDictionary("SettingsControls.xaml");
            var style = Assert.IsType<Style>(controls["SettingsThemeChoiceStyle"]);
            var template = Assert.IsType<ControlTemplate>(FindSetter(style, Control.TemplateProperty).Value);
            var selected = FindTrigger(template, ToggleButton.IsCheckedProperty, true);

            Assert.Equal("AccentBrush", DynamicResourceKey(FindTriggerSetter(selected, Border.BorderBrushProperty).Value));
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
        "Surface.App",
        "Surface.Content",
        "Surface.Subtle",
        "Surface.Hover",
        "Surface.Pressed",
        "SurfaceBrush",
        "SurfaceRaisedBrush",
        "Text.Primary",
        "Text.Secondary",
        "TextBrush",
        "TextOnAccentBrush",
        "Action.Primary",
        "Action.OnPrimary",
        "Accent.Relation",
        "State.Success",
        "State.Pending",
        "State.Danger",
        "Border.Decorative",
        "Border.Control",
        "Focus.Ring",
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
        "SettingsPasswordBoxStyle",
        "SettingsComboBoxStyle",
        "SettingsListBoxStyle",
        "SettingsScrollBarStyle",
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

    private static ResourceDictionary LoadDictionary(string fileName)
    {
        return LoadComponentDictionary($"Themes/{fileName}");
    }

    private static ResourceDictionary LoadComponentDictionary(string componentPath)
    {
        Application.ResourceAssembly ??= typeof(FgoPet.App.App).Assembly;
        return new ResourceDictionary
        {
            Source = new Uri($"/FgoPet.App;component/{componentPath}", UriKind.Relative),
        };
    }

    private static Color ResolvedBrushColor(Border host, string resourceKey)
    {
        host.SetResourceReference(Border.BackgroundProperty, resourceKey);
        return Assert.IsType<SolidColorBrush>(host.Background).Color;
    }

    private static void AssertColor(ResourceDictionary dictionary, string key, string expected) =>
        Assert.Equal(expected, ColorOf(dictionary, key).ToString());

    private static void AssertCompatibilityColor(ResourceDictionary dictionary, string semanticKey, params string[] compatibilityKeys)
    {
        var semantic = ColorOf(dictionary, semanticKey);
        foreach (var key in compatibilityKeys)
        {
            Assert.Equal(semantic, ColorOf(dictionary, key));
        }
    }

    private static Color ColorOf(ResourceDictionary dictionary, string key) =>
        Assert.IsType<Color>(dictionary[key]);

    private static double ContrastRatio(Color foreground, Color background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
        }

        var foregroundLuminance = Luminance(foreground);
        var backgroundLuminance = Luminance(background);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
               (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    }

    private static Setter FindSetter(Style style, DependencyProperty property) =>
        Assert.Single(style.Setters.OfType<Setter>().Where(setter => setter.Property == property));

    private static Trigger FindTrigger(ControlTemplate template, DependencyProperty property, object value) =>
        Assert.Single(template.Triggers.OfType<Trigger>()
            .Where(trigger => trigger.Property == property && Equals(trigger.Value, value)));

    private static Setter FindTriggerSetter(Trigger trigger, DependencyProperty property) =>
        Assert.Single(trigger.Setters.OfType<Setter>().Where(setter => setter.Property == property));

    private static string DynamicResourceKey(object? value)
    {
        Assert.NotNull(value);
        var resourceKeyProperty = value!.GetType().GetProperty(
            "ResourceKey",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(resourceKeyProperty);
        var resourceKey = resourceKeyProperty!.GetValue(value)?.ToString();
        Assert.NotNull(resourceKey);
        return resourceKey!;
    }

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
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
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
