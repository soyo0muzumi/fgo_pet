using FgoPet.Core.Panels;

namespace FgoPet.App.Main;

/// <summary>Single source of truth for the approved terminal panel's DIP budgets.</summary>
internal static class AttachedPanelVisualMetrics
{
    public const double ExpandedReservedHeight = 370;

    public static double CalculateWidth(double portraitWidth) =>
        Math.Clamp(portraitWidth + 100, 300, 340);

    public static double CalculateHeight(
        AttachedPanelState state,
        bool compactTimerVisible,
        bool customPresetVisible,
        double workAreaHeightDip)
    {
        var desired = state switch
        {
            AttachedPanelState.ExpandedFocus when customPresetVisible => 370,
            AttachedPanelState.ExpandedFocus => 240,
            AttachedPanelState.ExpandedToday or AttachedPanelState.ExpandedTodo or AttachedPanelState.ExpandedDialogue => 260,
            _ when compactTimerVisible => 170,
            _ => 150,
        };
        return Math.Min(desired, workAreaHeightDip * 0.6);
    }

    public static double CalculateReservedHeight(double workAreaHeightDip) =>
        Math.Min(ExpandedReservedHeight, workAreaHeightDip * 0.6);
}
