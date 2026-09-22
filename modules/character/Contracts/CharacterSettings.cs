using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;

namespace FgoPet.Character.Settings;

public sealed record CharacterSettings
{
    public PortraitSelection? Selection { get; init; }
    public double Scale { get; init; } = 0.50;
    public bool Topmost { get; init; } = true;
    public bool AutoCollapseExpandedPanel { get; init; } = true;
    public IReadOnlyDictionary<string, ServantPreference> ServantPreferences { get; init; } =
        new Dictionary<string, ServantPreference>(StringComparer.Ordinal);
    public UserProfile? UserProfile { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageSettings { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public static CharacterSettings Defaults { get; } = new();
}

public interface ICharacterSettingsStore
{
    CharacterSettings Load();
    void Save(CharacterSettings settings);
}
