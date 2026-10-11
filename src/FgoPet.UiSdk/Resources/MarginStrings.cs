namespace FgoPet.UiSdk;

/// <summary>English source strings for native Margin entry and recovery controls.</summary>
public static class MarginStrings
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["entry.chat"] = "Chat", ["entry.settings"] = "Settings", ["entry.hide"] = "Hide character",
        ["entry.today"] = "Today", ["entry.focus"] = "Focus",
        ["entry.restore"] = "Show / hide character", ["entry.exit"] = "Exit",
        ["entry.packFolder"] = "Open character pack folder",
        ["chat.unavailable"] = "Chat could not be opened.", ["action.retry"] = "Retry",
        ["chat.noCharacter"] = "Choose a character", ["chat.currentCharacter"] = "Current character",
        ["chat.choose.workspace"] = "Choose a workspace to authorize for this conversation",
        ["action.closePanel"] = "Close panel",
    };
    public static string Get(string key) => English[key];
}
