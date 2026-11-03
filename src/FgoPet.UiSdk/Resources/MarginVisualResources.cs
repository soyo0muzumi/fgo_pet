using FgoPet.App.Theming;
using FgoPet.Core.Settings;

namespace FgoPet.UiSdk;

/// <summary>Shared Margin tokens selected from the host's effective theme.</summary>
public static class MarginVisualResources
{
    // A surface without a ThemeService still has a deterministic, accessible palette.
    public static IReadOnlyDictionary<string, string> WebPalette { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["--surface"] = "#FBFAF8", ["--page"] = "#FCFBF9",
            ["--paper"] = "#FBFAF8", ["--paper-sunk"] = "#F2F0EB", ["--paper-edge"] = "#EDEAE4",
            ["--text"] = "#221F1B", ["--ink"] = "#221F1B", ["--ink-2"] = "#45403A",
            ["--muted"] = "#6E6961", ["--faint"] = "#776E64", ["--line"] = "#E1DCD3",
            ["--rule"] = "#E1DCD3", ["--rule-2"] = "#CFC8BC",
            ["--accent"] = "#9E6078", ["--accent-soft"] = "rgba(178,113,139,.12)",
            ["--accent-line"] = "rgba(178,113,139,.42)", ["--on-accent"] = "#FFFFFF",
            ["--danger"] = "#A85460", ["--ok"] = "#5E7F63",
            ["--panel"] = "#FFFEFD", ["--sunken"] = "#F2F0EB", ["--soft-rule"] = "#EEEAE4",
            ["--accent-deep"] = "#8F4E68", ["--primary"] = "#9E6078", ["--primary-hover"] = "#895167",
            ["--rose"] = "#F3E7EB", ["--rose-soft"] = "#FAF3F5", ["--rose-line"] = "#E6D2DB",
            ["--error"] = "#A85460", ["--sage"] = "#5E7F63", ["--color-scheme"] = "light",
        });

    private static IReadOnlyDictionary<string, string> DarkWebPalette { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["--surface"] = "#292629", ["--page"] = "#201E20",
            ["--paper"] = "#292629", ["--paper-sunk"] = "#343033", ["--paper-edge"] = "#3B3538",
            ["--text"] = "#F2EDE6", ["--ink"] = "#F2EDE6", ["--ink-2"] = "#D9D0CA",
            ["--muted"] = "#BFB6B1", ["--faint"] = "#A69A97", ["--line"] = "#494144",
            ["--rule"] = "#494144", ["--rule-2"] = "#74666D",
            ["--accent"] = "#CE92AA", ["--accent-soft"] = "rgba(206,146,170,.14)",
            ["--accent-line"] = "#614451", ["--on-accent"] = "#261B20",
            ["--danger"] = "#E6AB9B", ["--ok"] = "#A8B69B",
            ["--panel"] = "#292629", ["--sunken"] = "#343033", ["--soft-rule"] = "#3B3538",
            ["--accent-deep"] = "#E1A7BD", ["--primary"] = "#CE92AA", ["--primary-hover"] = "#E1A7BD",
            ["--rose"] = "#48323D", ["--rose-soft"] = "#35292F", ["--rose-line"] = "#614451",
            ["--error"] = "#E6AB9B", ["--sage"] = "#A8B69B", ["--color-scheme"] = "dark",
        });

    /// <summary>Follows the saved selection and resolved Windows theme without a second UI preference.</summary>
    public static IReadOnlyDictionary<string, string> CaptureWebPalette(ThemeService? theme) =>
        theme?.CurrentTheme == AppTheme.FgoDark ? DarkWebPalette : WebPalette;
}
