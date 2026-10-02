using FgoPet.Core.Settings;

namespace FgoPet.UiFoundation.Theming;

public sealed record ThemeSettings(AppTheme Theme)
{
    public static ThemeSettings Defaults { get; } = new(AppTheme.FgoLight);
}

public interface IThemeSettingsStore
{
    ThemeSettings Load();
    void Save(ThemeSettings settings);
}
