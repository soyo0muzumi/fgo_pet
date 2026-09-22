using FgoPet.Core.Speech;

namespace FgoPet.Speech.Settings;

public sealed record SpeechSettings(SpeechConnectionSettings Connection)
{
    public static SpeechSettings Defaults { get; } = new(SpeechConnectionSettings.Defaults);
}

public interface ISpeechSettingsStore
{
    SpeechSettings Load();
    void Save(SpeechSettings settings);
}
