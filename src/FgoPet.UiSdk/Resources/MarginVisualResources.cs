namespace FgoPet.UiSdk;

/// <summary>Frozen Margin v5 paper palette for the Chat and Settings surfaces.</summary>
public static class MarginVisualResources
{
    public static IReadOnlyDictionary<string, string> WebPalette { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["--surface"] = "#FBFAF8", ["--page"] = "#FCFBF9",
            ["--paper"] = "#FBFAF8", ["--paper-sunk"] = "#F2F0EB", ["--paper-edge"] = "#EDEAE4",
            ["--text"] = "#221F1B", ["--ink"] = "#221F1B", ["--ink-2"] = "#45403A",
            ["--muted"] = "#6E6961", ["--faint"] = "#9B958B", ["--line"] = "#E1DCD3",
            ["--rule"] = "#E1DCD3", ["--rule-2"] = "#CFC8BC",
            ["--accent"] = "#B2718B", ["--accent-soft"] = "rgba(178,113,139,.14)",
            ["--accent-line"] = "rgba(178,113,139,.42)", ["--on-accent"] = "#FFFFFF",
            ["--danger"] = "#A85460", ["--ok"] = "#5E7F63",
        });
}
