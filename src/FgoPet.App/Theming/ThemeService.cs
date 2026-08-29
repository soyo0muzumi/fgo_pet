using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FgoPet.Core.Settings;

namespace FgoPet.App.Theming;

/// <summary>Loads the selected settings palette and persists it without replacing shared resources.</summary>
public sealed class ThemeService
{
    public const string ThemeDictionaryMarker = "FgoPet.ThemeDictionary";

    private readonly IAppSettingsStore _settings;
    private readonly ResourceDictionary _resources;
    private readonly Func<AppTheme, ResourceDictionary> _resourceLoader;
    private readonly Dispatcher? _dispatcher;

    public ThemeService(IAppSettingsStore settings)
        : this(settings, ResolveApplicationResources(), null)
    {
    }

    public ThemeService(IAppSettingsStore settings, ResourceDictionary resources)
        : this(settings, resources, null)
    {
    }

    public ThemeService(
        IAppSettingsStore settings,
        ResourceDictionary resources,
        Func<AppTheme, ResourceDictionary>? resourceLoader)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _resourceLoader = resourceLoader ?? LoadThemeDictionary;
        _dispatcher = Application.Current?.Dispatcher;
        CurrentTheme = AppTheme.ModernGray;
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
        var savedTheme = AppTheme.ModernGray;
        var loadStatus = string.Empty;
        try
        {
            savedTheme = Normalize(_settings.Load().Theme);
        }
        catch (Exception)
        {
            // A settings read failure should not prevent the shell from starting with its default palette.
            loadStatus = $"设置读取失败，已使用{DisplayName(AppTheme.ModernGray)}";
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
                CurrentTheme = AppTheme.ModernGray;
                StatusText = "主题资源加载失败，已使用现代灰回退";
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
        var dictionary = new ResourceDictionary
        {
            [ThemeDictionaryMarker] = AppTheme.ModernGray.ToString(),
        };

        AddBrushes(dictionary, Color.FromRgb(27, 33, 43), "WindowBackgroundBrush", "BackgroundBrush", "ContentBackgroundBrush");
        AddBrushes(dictionary, Color.FromRgb(242, 245, 248), "WindowForegroundBrush", "TextBrush", "PrimaryTextBrush");
        AddBrush(dictionary, "SurfaceBrush", Color.FromRgb(37, 44, 55));
        AddBrushes(dictionary, Color.FromRgb(48, 57, 70), "SurfaceRaisedBrush", "SurfaceAltBrush");
        AddBrush(dictionary, "SurfaceHoverBrush", Color.FromRgb(58, 68, 82));
        AddBrush(dictionary, "SurfacePressedBrush", Color.FromRgb(70, 83, 100));
        AddBrushes(dictionary, Color.FromRgb(23, 29, 37), "SidebarBrush", "SidebarBackgroundBrush");
        AddBrushes(dictionary, Color.FromRgb(181, 192, 206), "SidebarForegroundBrush", "SidebarTextBrush");
        AddBrush(dictionary, "SidebarSelectedBrush", Color.FromRgb(49, 64, 82));
        AddBrush(dictionary, "SidebarSelectedTextBrush", Color.FromRgb(242, 246, 251));
        AddBrush(dictionary, "SidebarHoverBrush", Color.FromRgb(34, 43, 54));
        AddBrushes(dictionary, Color.FromRgb(176, 188, 201), "MutedTextBrush", "SecondaryTextBrush");
        AddBrushes(dictionary, Color.FromRgb(126, 139, 154), "SubtleTextBrush", "TertiaryTextBrush");
        AddBrush(dictionary, "TextOnAccentBrush", Color.FromRgb(16, 24, 32));
        AddBrush(dictionary, "AccentBrush", Color.FromRgb(110, 183, 232));
        AddBrush(dictionary, "AccentHoverBrush", Color.FromRgb(137, 200, 240));
        AddBrush(dictionary, "AccentPressedBrush", Color.FromRgb(78, 159, 213));
        AddBrush(dictionary, "AccentSoftBrush", Color.FromArgb(51, 110, 183, 232));
        AddBrush(dictionary, "BorderBrush", Color.FromRgb(65, 77, 92));
        AddBrush(dictionary, "BorderStrongBrush", Color.FromRgb(101, 116, 135));
        AddBrush(dictionary, "DividerBrush", Color.FromArgb(51, 65, 77, 92));
        AddBrush(dictionary, "FocusBrush", Color.FromRgb(154, 213, 244));
        AddBrush(dictionary, "WarningBrush", Color.FromRgb(229, 183, 91));
        AddBrush(dictionary, "WarningSoftBrush", Color.FromArgb(51, 229, 183, 91));
        AddBrush(dictionary, "DangerBrush", Color.FromRgb(227, 115, 131));
        AddBrush(dictionary, "DangerHoverBrush", Color.FromRgb(242, 138, 152));
        AddBrush(dictionary, "DangerSoftBrush", Color.FromArgb(51, 227, 115, 131));
        AddBrush(dictionary, "SuccessBrush", Color.FromRgb(121, 201, 156));
        AddBrush(dictionary, "SuccessSoftBrush", Color.FromArgb(51, 121, 201, 156));
        AddBrush(dictionary, "CardShadowBrush", Color.FromArgb(102, 0, 0, 0));
        AddBrush(dictionary, "InputBackgroundBrush", Color.FromRgb(32, 39, 51));
        AddBrush(dictionary, "InputDisabledBrush", Color.FromRgb(43, 50, 61));
        AddBrush(dictionary, "DisabledTextBrush", Color.FromRgb(109, 120, 134));
        AddBrush(dictionary, "DisabledBorderBrush", Color.FromRgb(55, 65, 77));
        AddBrush(dictionary, "OverlayBrush", Color.FromArgb(153, 10, 14, 20));
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
        Enum.IsDefined(theme) ? theme : AppTheme.ModernGray;

    private static string DisplayName(AppTheme theme) =>
        theme == AppTheme.FgoLight ? "FGO Light" : "现代灰";
}
