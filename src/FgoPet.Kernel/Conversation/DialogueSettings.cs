using FgoPet.Core.Settings;

namespace FgoPet.Dialogue.Settings;

public sealed record DialogueSettings(ModelConnectionSettings? ModelConnection, bool ShowReasoning)
{
    public static DialogueSettings Defaults { get; } = new(null, true);
}

public interface IDialogueSettingsStore
{
    DialogueSettings Load();
    void Save(DialogueSettings settings);
}
