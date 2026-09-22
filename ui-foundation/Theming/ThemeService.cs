using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FgoPet.Core.Settings;
using FgoPet.UiFoundation.Theming;

namespace FgoPet.App.Theming;

/// <summary>Loads the selected settings palette and persists it without replacing shared resources.</summary>
public sealed class ThemeService
{
    public const string ThemeDictionaryMarker = "FgoPet.ThemeDictionary";

    private readonly IThemeSettingsStore _settings;
    private readonly ResourceDictionary _resources;
    private readonly Func<AppTheme, ResourceDictionary> _resourceLoader;
    private readonly Dispatcher? _dispatcher;

    public ThemeService(IThemeSettingsStore settings)
        : this(settings, ResolveApplicationResources(), null)
    {
    }

    public ThemeService(IThemeSettingsStore settings, ResourceDictionary resources)
        : this(settings, resources, null)
    {
    }

    public ThemeService(
        IThemeSettingsStore settings,
        ResourceDictionary resources,
        Func<AppTheme, ResourceDictionary>? resourceLoader)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _resourceLoader = resourceLoader ?? LoadThemeDictionary;
        _dispatcher = Application.Current?.Dispatcher;
        CurrentTheme = AppTheme.FgoLight;
        StatusText = "主题尚未初始化";
    }

    public AppTheme CurrentTheme { get; private set; }

    public string StatusText { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Initialize()
    {
        InvokeOnResourceDispatcher(InitializeCore);
    }

    public void Select(AppTheme theme)
    {
        InvokeOnResourceDispatcher(() => SelectCore(Normalize(theme)));
    }

    internal static bool IsMarkedThemeDictionary(ResourceDictionary dictionary) =>
        dictionary.Contains(ThemeDictionaryMarker) || IsThemeSource(dictionary.Source);

    internal static AppTheme GetMarkedTheme(ResourceDictionary dictionary)
    {
        if (dictionary[ThemeDictionaryMarker] is AppTheme theme)
        {
            return Normalize(theme);
        }

        if (dictionary[ThemeDictionaryMarker] is string name &&
            Enum.TryParse<AppTheme>(name, ignoreCase: true, out var parsed))
        {
            return Normalize(parsed);
        }

        var source = dictionary.Source?.OriginalString;
        if (source?.Contains("FgoLight.xaml", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AppTheme.FgoLight;
        }

        return AppTheme.ModernGray;
    }

    internal static ResourceDictionary CreateTestDictionary(AppTheme theme) =>
        new()
        {
            [ThemeDictionaryMarker] = theme.ToString(),
            ["WindowBackgroundBrush"] = new SolidColorBrush(theme == AppTheme.FgoLight ? Colors.White : Colors.Black),
        };

    private void InitializeCore()
    {
        MarkThemeSources();
        var savedTheme = AppTheme.FgoLight;
        var loadStatus = string.Empty;
        try
        {
            savedTheme = Normalize(_settings.Load().Theme);
        }
        catch (Exception)
        {
            // A settings read failure should not prevent the shell from starting with its default palette.
            loadStatus = $"设置读取失败，已使用{DisplayName(AppTheme.FgoLight)}";
        }

        if (TryApplyDictionary(savedTheme, allowFallback: true))
        {
            StatusText = string.IsNullOrEmpty(loadStatus)
                ? $"当前主题：{DisplayName(CurrentTheme)}"
                : loadStatus;
        }
        else if (!string.IsNullOrEmpty(loadStatus))
        {
            StatusText = loadStatus;
        }
    }

    private void SelectCore(AppTheme theme)
    {
        if (!TryApplyDictionary(theme, allowFallback: false))
        {
            return;
        }

        try
        {
            var settings = _settings.Load();
            _settings.Save(settings with { Theme = theme });
            StatusText = $"已切换至{DisplayName(theme)}";
        }
        catch (Exception)
        {
            // The in-process palette is already valid; persistence can be retried by a later selection.
            StatusText = $"已应用{DisplayName(theme)}，保存失败";
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryApplyDictionary(AppTheme theme, bool allowFallback)
    {
        ResourceDictionary next;
        try
        {
            next = _resourceLoader(theme) ?? throw new InvalidOperationException("主题资源为空。");
            next[ThemeDictionaryMarker] = theme.ToString();
        }
        catch (Exception)
        {
            var existing = FindMarkedThemeDictionary();
            if (existing is not null)
            {
                CurrentTheme = GetMarkedTheme(existing);
                StatusText = "主题资源加载失败，保留当前主题";
            }
            else if (allowFallback)
            {
                next = CreateFallbackDictionary();
                _resources.MergedDictionaries.Add(next);
                CurrentTheme = AppTheme.FgoLight;
                StatusText = "主题资源加载失败，已使用浅色回退";
            }
            else
            {
                StatusText = "主题资源加载失败，保留当前主题";
            }

            return false;
        }

        // Add first so DynamicResource lookups never observe a gap between palettes.
        _resources.MergedDictionaries.Add(next);
        foreach (var previous in _resources.MergedDictionaries
                     .Where(IsMarkedThemeDictionary)
                     .Where(dictionary => !ReferenceEquals(dictionary, next))
                     .ToArray())
        {
            _resources.MergedDictionaries.Remove(previous);
        }

        CurrentTheme = theme;
        return true;
    }

    private ResourceDictionary? FindMarkedThemeDictionary() =>
        _resources.MergedDictionaries.FirstOrDefault(IsMarkedThemeDictionary);

    private void MarkThemeSources()
    {
        foreach (var dictionary in _resources.MergedDictionaries
                     .Where(dictionary => IsThemeSource(dictionary.Source))
                     .ToArray())
        {
            var theme = GetMarkedTheme(dictionary);
            dictionary[ThemeDictionaryMarker] = theme.ToString();
        }

        if (FindMarkedThemeDictionary() is { } existing)
        {
            CurrentTheme = GetMarkedTheme(existing);
        }
    }

    private void InvokeOnResourceDispatcher(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    private static ResourceDictionary ResolveApplicationResources() =>
        Application.Current?.Resources
        ?? throw new InvalidOperationException("ThemeService requires an active WPF Application.");

    private static ResourceDictionary LoadThemeDictionary(AppTheme theme)
    {
        var fileName = theme == AppTheme.FgoLight ? "FgoLight.xaml" : "ModernGray.xaml";
        return new ResourceDictionary
        {
            Source = new Uri($"/FgoPet.App;component/Themes/{fileName}", UriKind.Relative),
        };
    }

    private static ResourceDictionary CreateFallbackDictionary()
    {
        var dictionary = new ResourceDictionary { [ThemeDictionaryMarker] = AppTheme.FgoLight.ToString() };
        dictionary["Semantic.AppColor"] = (Color)ColorConverter.ConvertFromString("#FFF7F6FB");
        dictionary["Semantic.ContentColor"] = (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
        dictionary["Semantic.SubtleColor"] = (Color)ColorConverter.ConvertFromString("#FFEEE8FA");
        dictionary["Semantic.HoverColor"] = (Color)ColorConverter.ConvertFromString("#FFE5DCF4");
        dictionary["Semantic.PressedColor"] = (Color)ColorConverter.ConvertFromString("#FFD9CCEE");
        dictionary["Semantic.PrimaryTextColor"] = (Color)ColorConverter.ConvertFromString("#FF252137");
        dictionary["Semantic.SecondaryTextColor"] = (Color)ColorConverter.ConvertFromString("#FF6D687B");
        dictionary["Semantic.TertiaryTextColor"] = (Color)ColorConverter.ConvertFromString("#FF817A8F");
        dictionary["Semantic.PrimaryActionColor"] = (Color)ColorConverter.ConvertFromString("#FF7050B8");
        dictionary["Semantic.OnPrimaryActionColor"] = (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
        dictionary["Semantic.ActionHoverColor"] = (Color)ColorConverter.ConvertFromString("#FF8062C3");
        dictionary["Semantic.ActionPressedColor"] = (Color)ColorConverter.ConvertFromString("#FF60429F");
        dictionary["Semantic.ActionSoftColor"] = (Color)ColorConverter.ConvertFromString("#337050B8");
        dictionary["Semantic.BorderDecorativeColor"] = (Color)ColorConverter.ConvertFromString("#FFD8D1E2");
        dictionary["Semantic.BorderControlColor"] = (Color)ColorConverter.ConvertFromString("#FF827A70");
        dictionary["Semantic.FocusColor"] = (Color)ColorConverter.ConvertFromString("#FF5B3F9A");
        dictionary["Semantic.RelationColor"] = (Color)ColorConverter.ConvertFromString("#FFA58B57");
        dictionary["Semantic.OverlayColor"] = (Color)ColorConverter.ConvertFromString("#660D0A14");
        dictionary["WindowBackgroundColor"] = (Color)ColorConverter.ConvertFromString("#FFF7F6FB");
        dictionary["SurfaceColor"] = (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
        dictionary["SurfaceRaisedColor"] = (Color)ColorConverter.ConvertFromString("#FFEEE8FA");
        dictionary["SurfaceHoverColor"] = (Color)ColorConverter.ConvertFromString("#FFE5DCF4");
        dictionary["SurfacePressedColor"] = (Color)ColorConverter.ConvertFromString("#FFD9CCEE");
        dictionary["SidebarColor"] = (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
        dictionary["SidebarForegroundColor"] = (Color)ColorConverter.ConvertFromString("#FF6D687B");
        dictionary["SidebarSelectedColor"] = (Color)ColorConverter.ConvertFromString("#FFEEE8FA");
        dictionary["SidebarSelectedTextColor"] = (Color)ColorConverter.ConvertFromString("#FF252137");
        dictionary["SidebarHoverColor"] = (Color)ColorConverter.ConvertFromString("#FFE5DCF4");
        dictionary["TextColor"] = (Color)ColorConverter.ConvertFromString("#FF252137");
        dictionary["MutedTextColor"] = (Color)ColorConverter.ConvertFromString("#FF6D687B");
        dictionary["SubtleTextColor"] = (Color)ColorConverter.ConvertFromString("#FF817A8F");
        dictionary["TextOnAccentColor"] = (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
        dictionary["AccentColor"] = (Color)ColorConverter.ConvertFromString("#FF7050B8");
        dictionary["AccentHoverColor"] = (Color)ColorConverter.ConvertFromString("#FF8062C3");
        dictionary["AccentPressedColor"] = (Color)ColorConverter.ConvertFromString("#FF60429F");
        dictionary["AccentSoftColor"] = (Color)ColorConverter.ConvertFromString("#337050B8");
        dictionary["BorderColor"] = (Color)ColorConverter.ConvertFromString("#FFD8D1E2");
        dictionary["BorderStrongColor"] = (Color)ColorConverter.ConvertFromString("#FF827A70");
        dictionary["DividerColor"] = (Color)ColorConverter.ConvertFromString("#33D8D1E2");
        dictionary["FocusColor"] = (Color)ColorConverter.ConvertFromString("#FF5B3F9A");
        dictionary["WarningColor"] = (Color)ColorConverter.ConvertFromString("#FFAA7626");
        dictionary["WarningSoftColor"] = (Color)ColorConverter.ConvertFromString("#33AA7626");
        dictionary["DangerColor"] = (Color)ColorConverter.ConvertFromString("#FFB44C5D");
        dictionary["DangerHoverColor"] = (Color)ColorConverter.ConvertFromString("#FFD06978");
        dictionary["DangerSoftColor"] = (Color)ColorConverter.ConvertFromString("#33B44C5D");
        dictionary["SuccessColor"] = (Color)ColorConverter.ConvertFromString("#FF2D805D");
        dictionary["SuccessSoftColor"] = (Color)ColorConverter.ConvertFromString("#332D805D");
        dictionary["CardShadowColor"] = (Color)ColorConverter.ConvertFromString("#263D536C");
        dictionary["InputBackgroundColor"] = (Color)ColorConverter.ConvertFromString("#FFF7F9FB");
        dictionary["InputDisabledColor"] = (Color)ColorConverter.ConvertFromString("#FFE8EDF2");
        dictionary["DisabledTextColor"] = (Color)ColorConverter.ConvertFromString("#FF9AA7B4");
        dictionary["DisabledBorderColor"] = (Color)ColorConverter.ConvertFromString("#FFD6DEE7");
        dictionary["OverlayColor"] = (Color)ColorConverter.ConvertFromString("#660D0A14");
        dictionary["ShellWindowColor"] = (Color)ColorConverter.ConvertFromString("#FFF7F6FB");
        dictionary["ShellPanelColor"] = (Color)ColorConverter.ConvertFromString("#FFEEE8FA");
        dictionary["ShellRaisedColor"] = (Color)ColorConverter.ConvertFromString("#FFFFFFFF");
        dictionary["ShellTextColor"] = (Color)ColorConverter.ConvertFromString("#FF252137");
        dictionary["ShellMutedColor"] = (Color)ColorConverter.ConvertFromString("#FF6D687B");
        dictionary["ShellLineColor"] = (Color)ColorConverter.ConvertFromString("#FF827A70");
        dictionary["ShellAccentColor"] = (Color)ColorConverter.ConvertFromString("#FF7050B8");
        dictionary["ShellAccentSoftColor"] = (Color)ColorConverter.ConvertFromString("#337050B8");
        dictionary["ShellOverlayColor"] = (Color)ColorConverter.ConvertFromString("#660D0A14");

        AddBrush(dictionary, "WindowBackgroundBrush", (Color)ColorConverter.ConvertFromString("#FFF7F6FB"));
        AddBrush(dictionary, "BackgroundBrush", (Color)ColorConverter.ConvertFromString("#FFF7F6FB"));
        AddBrush(dictionary, "ContentBackgroundBrush", (Color)ColorConverter.ConvertFromString("#FFF7F6FB"));
        AddBrush(dictionary, "WindowForegroundBrush", (Color)ColorConverter.ConvertFromString("#FF252137"));
        AddBrush(dictionary, "SurfaceBrush", (Color)ColorConverter.ConvertFromString("#FFFFFFFF"));
        AddBrush(dictionary, "SurfaceRaisedBrush", (Color)ColorConverter.ConvertFromString("#FFEEE8FA"));
        AddBrush(dictionary, "SurfaceAltBrush", (Color)ColorConverter.ConvertFromString("#FFEEE8FA"));
        AddBrush(dictionary, "SurfaceHoverBrush", (Color)ColorConverter.ConvertFromString("#FFE5DCF4"));
        AddBrush(dictionary, "SurfacePressedBrush", (Color)ColorConverter.ConvertFromString("#FFD9CCEE"));
        AddBrush(dictionary, "SidebarBrush", (Color)ColorConverter.ConvertFromString("#FFFFFFFF"));
        AddBrush(dictionary, "SidebarBackgroundBrush", (Color)ColorConverter.ConvertFromString("#FFFFFFFF"));
        AddBrush(dictionary, "SidebarForegroundBrush", (Color)ColorConverter.ConvertFromString("#FF6D687B"));
        AddBrush(dictionary, "SidebarTextBrush", (Color)ColorConverter.ConvertFromString("#FF6D687B"));
        AddBrush(dictionary, "SidebarSelectedBrush", (Color)ColorConverter.ConvertFromString("#FFEEE8FA"));
        AddBrush(dictionary, "SidebarSelectedTextBrush", (Color)ColorConverter.ConvertFromString("#FF252137"));
        AddBrush(dictionary, "SidebarHoverBrush", (Color)ColorConverter.ConvertFromString("#FFE5DCF4"));
        AddBrush(dictionary, "TextBrush", (Color)ColorConverter.ConvertFromString("#FF252137"));
        AddBrush(dictionary, "PrimaryTextBrush", (Color)ColorConverter.ConvertFromString("#FF252137"));
        AddBrush(dictionary, "MutedTextBrush", (Color)ColorConverter.ConvertFromString("#FF6D687B"));
        AddBrush(dictionary, "SecondaryTextBrush", (Color)ColorConverter.ConvertFromString("#FF6D687B"));
        AddBrush(dictionary, "SubtleTextBrush", (Color)ColorConverter.ConvertFromString("#FF817A8F"));
        AddBrush(dictionary, "TertiaryTextBrush", (Color)ColorConverter.ConvertFromString("#FF817A8F"));
        AddBrush(dictionary, "TextOnAccentBrush", (Color)ColorConverter.ConvertFromString("#FFFFFFFF"));
        AddBrush(dictionary, "AccentBrush", (Color)ColorConverter.ConvertFromString("#FF7050B8"));
        AddBrush(dictionary, "AccentHoverBrush", (Color)ColorConverter.ConvertFromString("#FF8062C3"));
        AddBrush(dictionary, "AccentPressedBrush", (Color)ColorConverter.ConvertFromString("#FF60429F"));
        AddBrush(dictionary, "AccentSoftBrush", (Color)ColorConverter.ConvertFromString("#337050B8"));
        AddBrush(dictionary, "BorderBrush", (Color)ColorConverter.ConvertFromString("#FFD8D1E2"));
        AddBrush(dictionary, "BorderStrongBrush", (Color)ColorConverter.ConvertFromString("#FF827A70"));
        AddBrush(dictionary, "DividerBrush", (Color)ColorConverter.ConvertFromString("#33D8D1E2"));
        AddBrush(dictionary, "FocusBrush", (Color)ColorConverter.ConvertFromString("#FF5B3F9A"));
        AddBrush(dictionary, "WarningBrush", (Color)ColorConverter.ConvertFromString("#FFAA7626"));
        AddBrush(dictionary, "WarningSoftBrush", (Color)ColorConverter.ConvertFromString("#33AA7626"));
        AddBrush(dictionary, "DangerBrush", (Color)ColorConverter.ConvertFromString("#FFB44C5D"));
        AddBrush(dictionary, "DangerHoverBrush", (Color)ColorConverter.ConvertFromString("#FFD06978"));
        AddBrush(dictionary, "DangerSoftBrush", (Color)ColorConverter.ConvertFromString("#33B44C5D"));
        AddBrush(dictionary, "SuccessBrush", (Color)ColorConverter.ConvertFromString("#FF2D805D"));
        AddBrush(dictionary, "SuccessSoftBrush", (Color)ColorConverter.ConvertFromString("#332D805D"));
        AddBrush(dictionary, "CardShadowBrush", (Color)ColorConverter.ConvertFromString("#263D536C"));
        AddBrush(dictionary, "InputBackgroundBrush", (Color)ColorConverter.ConvertFromString("#FFF7F9FB"));
        AddBrush(dictionary, "InputDisabledBrush", (Color)ColorConverter.ConvertFromString("#FFE8EDF2"));
        AddBrush(dictionary, "DisabledTextBrush", (Color)ColorConverter.ConvertFromString("#FF9AA7B4"));
        AddBrush(dictionary, "DisabledBorderBrush", (Color)ColorConverter.ConvertFromString("#FFD6DEE7"));
        AddBrush(dictionary, "OverlayBrush", (Color)ColorConverter.ConvertFromString("#660D0A14"));
        AddBrush(dictionary, "Surface.App", (Color)ColorConverter.ConvertFromString("#FFF7F6FB"));
        AddBrush(dictionary, "Surface.Content", (Color)ColorConverter.ConvertFromString("#FFFFFFFF"));
        AddBrush(dictionary, "Surface.Subtle", (Color)ColorConverter.ConvertFromString("#FFEEE8FA"));
        AddBrush(dictionary, "Surface.Hover", (Color)ColorConverter.ConvertFromString("#FFE5DCF4"));
        AddBrush(dictionary, "Surface.Pressed", (Color)ColorConverter.ConvertFromString("#FFD9CCEE"));
        AddBrush(dictionary, "Text.Primary", (Color)ColorConverter.ConvertFromString("#FF252137"));
        AddBrush(dictionary, "Text.Secondary", (Color)ColorConverter.ConvertFromString("#FF6D687B"));
        AddBrush(dictionary, "Action.Primary", (Color)ColorConverter.ConvertFromString("#FF7050B8"));
        AddBrush(dictionary, "Action.OnPrimary", (Color)ColorConverter.ConvertFromString("#FFFFFFFF"));
        AddBrush(dictionary, "Accent.Relation", (Color)ColorConverter.ConvertFromString("#FFA58B57"));
        AddBrush(dictionary, "State.Success", (Color)ColorConverter.ConvertFromString("#FF2D805D"));
        AddBrush(dictionary, "State.Pending", (Color)ColorConverter.ConvertFromString("#FFAA7626"));
        AddBrush(dictionary, "State.Danger", (Color)ColorConverter.ConvertFromString("#FFB44C5D"));
        AddBrush(dictionary, "Border.Decorative", (Color)ColorConverter.ConvertFromString("#FFD8D1E2"));
        AddBrush(dictionary, "Border.Control", (Color)ColorConverter.ConvertFromString("#FF827A70"));
        AddBrush(dictionary, "Focus.Ring", (Color)ColorConverter.ConvertFromString("#FF5B3F9A"));
        return dictionary;
    }

    private static void AddBrushes(ResourceDictionary dictionary, Color color, params string[] keys)
    {
        foreach (var key in keys)
        {
            AddBrush(dictionary, key, color);
        }
    }

    private static void AddBrush(ResourceDictionary dictionary, string key, Color color) =>
        dictionary[key] = new SolidColorBrush(color);

    private static bool IsThemeSource(Uri? source) =>
        source?.OriginalString.Contains("/Themes/ModernGray.xaml", StringComparison.OrdinalIgnoreCase) == true ||
        source?.OriginalString.Contains("/Themes/FgoLight.xaml", StringComparison.OrdinalIgnoreCase) == true;

    private static AppTheme Normalize(AppTheme theme) =>
        Enum.IsDefined(theme) ? theme : AppTheme.FgoLight;

    private static string DisplayName(AppTheme theme) =>
        theme == AppTheme.FgoLight ? "FGO Light" : "现代灰";
}
