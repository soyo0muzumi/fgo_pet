namespace FgoPet.Plugin.Focus.Desktop;

/// <summary>English source labels for the plugin-owned Margin Focus presentation.</summary>
public static class FocusStrings
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["focus.title"] = "Focus", ["focus.session"] = "Focus Session",
        ["focus.options"] = "Session options", ["focus.back"] = "Back",
        ["focus.duration"] = "Duration", ["focus.break"] = "Break", ["focus.cycles"] = "Cycles",
        ["focus.minuteUnit"] = "min",
        ["focus.start"] = "Start", ["focus.pause"] = "Pause", ["focus.resume"] = "Resume", ["focus.stop"] = "Stop",
        ["focus.running"] = "Focusing", ["focus.breaking"] = "Break", ["focus.paused"] = "Paused",
        ["focus.remaining"] = "{time} remaining", ["focus.controls"] = "Focus controls",
        ["focus.alreadyActive"] = "{state}. Resume or stop the current session before starting another.",
        ["focus.chooseCharacter"] = "Choose an active character first.", ["focus.invalidOptions"] = "Check duration (5–180 min), break (1–60 min) and cycles (1–12).",
        ["focus.unavailable"] = "Focus is unavailable.", ["focus.durationRange"] = "5–180 minutes",
        ["focus.breakRange"] = "1–60 minutes", ["focus.cyclesRange"] = "1–12 cycles",
        ["focus.quick10"] = "10 min", ["focus.quick25"] = "25 min", ["focus.quick50"] = "50 min",
    };
    public static string Get(string key) => English[key];
    public static string Title => Get("focus.title");
    public static string Session => Get("focus.session");
    public static string Options => Get("focus.options");
    public static string Back => Get("focus.back");
    public static string Duration => Get("focus.duration");
    public static string Break => Get("focus.break");
    public static string Cycles => Get("focus.cycles");
    public static string MinuteUnit => Get("focus.minuteUnit");
    public static string Start => Get("focus.start");
    public static string Stop => Get("focus.stop");
    public static string Controls => Get("focus.controls");
    public static string Quick10 => Get("focus.quick10");
    public static string Quick25 => Get("focus.quick25");
    public static string Quick50 => Get("focus.quick50");
}
