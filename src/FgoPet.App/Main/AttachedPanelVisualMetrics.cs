using FgoPet.Core.Panels;

namespace FgoPet.App.Main;

public static class AttachedPanelVisualMetrics
{
    public const double ExpandedReservedHeight = 312;

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
        var desired = quickActionsExpanded
            ? ExpandedReservedHeight
            : focusSurfaceVisible ? 220
            : compactTimerVisible ? 188 : 132;
        return Math.Min(desired, workAreaHeightDip * 0.72);
    }

    public static double CalculateReservedHeight(double workAreaHeightDip) =>
        Math.Min(ExpandedReservedHeight, workAreaHeightDip * 0.72);
}
