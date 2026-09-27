using FgoPet.Core.Panels;

namespace FgoPet.App.Main;

public static class AttachedPanelVisualMetrics
{
    public const double ExpandedReservedHeight = 312;

    /// <summary>The panel never exceeds this share of the work area; the host window applies the same limit.</summary>
    public const double WorkAreaRatio = 0.6;

    public static double CalculateWidth(double portraitWidth, bool focusSurfaceVisible = false) =>
        focusSurfaceVisible ? 304 : Math.Clamp(portraitWidth + 24, 230, 280);

    public static double CalculateHeight(
        AttachedPanelState state,
        bool compactTimerVisible,
        bool customPresetVisible,
        double workAreaHeightDip,
        bool quickActionsExpanded = false,
        bool focusSurfaceVisible = false)
    {
        // Budgets cover the shipped compact entry shell: 132 DIP for the three base
        // entries, 188 DIP while the 196 DIP timer ring is visible, 220 DIP for the
        // focus surface, and 312 DIP once the four quick-action entries are shown.
        var desired = quickActionsExpanded
            ? ExpandedReservedHeight
            : focusSurfaceVisible ? 220
            : compactTimerVisible ? 188 : 132;
        return Math.Min(desired, workAreaHeightDip * WorkAreaRatio);
    }

    public static double CalculateReservedHeight(double workAreaHeightDip) =>
        Math.Min(ExpandedReservedHeight, workAreaHeightDip * WorkAreaRatio);
}
