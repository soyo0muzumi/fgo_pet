using FgoPet.Core.Portraits;

namespace FgoPet.Core.Settings;

/// <summary>User preferences for the pet. Placement is stored separately.</summary>
public sealed record AppSettings(
    PortraitSelection? Selection,
    double Scale,
    bool Topmost,
    bool AutoCollapseExpandedPanel)
{
    private static readonly IReadOnlyDictionary<string, ServantPreference> EmptyServantPreferences =
        new Dictionary<string, ServantPreference>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyPackageSettings =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public ModelConnectionSettings? ModelConnection { get; init; }

    public bool MemoryEnabled { get; init; } = true;

    public IReadOnlyDictionary<string, ServantPreference> ServantPreferences { get; init; } = EmptyServantPreferences;

    public AppTheme Theme { get; init; } = AppTheme.ModernGray;

    public UserProfile? UserProfile { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageSettings { get; init; } = EmptyPackageSettings;

    public static AppSettings Defaults { get; } = new(
        Selection: null,
        Scale: 0.50,
        Topmost: true,
        AutoCollapseExpandedPanel: true);
}
