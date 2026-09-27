using FgoPet.Character.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Memory.Settings;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;

namespace FgoPet.SettingsHost;

internal sealed record SettingsDocumentSnapshot(
    CharacterSettings Character,
    DialogueSettings Dialogue,
    MemorySettings Memory,
    WorkExecutionSettings WorkExecution,
    SpeechSettings Speech,
    ThemeSettings Theme)
{
    public static SettingsDocumentSnapshot Defaults { get; } = new(
        CharacterSettings.Defaults,
        DialogueSettings.Defaults,
        MemorySettings.Defaults,
        WorkExecutionSettings.Defaults,
        SpeechSettings.Defaults,
        ThemeSettings.Defaults);
}
