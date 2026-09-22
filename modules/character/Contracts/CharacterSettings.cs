using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using System.Collections.Immutable;

namespace FgoPet.Character.Settings;

public sealed record CharacterSettings
{
    private static readonly IReadOnlyDictionary<string, ServantPreference> EmptyServantPreferences =
        ImmutableDictionary.Create<string, ServantPreference>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyPackageSettings =
        ImmutableDictionary.Create<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public PortraitSelection? Selection { get; init; }
    public double Scale { get; init; } = 0.50;
    public bool Topmost { get; init; } = true;
    public bool AutoCollapseExpandedPanel { get; init; } = true;
    public IReadOnlyDictionary<string, ServantPreference> ServantPreferences { get; init; } =
        EmptyServantPreferences;
    public UserProfile? UserProfile { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageSettings { get; init; } =
        EmptyPackageSettings;

    public static CharacterSettings Defaults { get; } = new();
}

public interface ICharacterSettingsStore
{
    CharacterSettings Load();
    void Save(CharacterSettings settings);
}
