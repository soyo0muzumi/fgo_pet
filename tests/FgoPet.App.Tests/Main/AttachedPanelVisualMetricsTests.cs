using FgoPet.App.Main;
using FgoPet.Core.Panels;
using Xunit;

namespace FgoPet.App.Tests.Main;

public sealed class AttachedPanelVisualMetricsTests
{
    [Theory]
    [InlineData(100, 300)]
    [InlineData(220, 320)]
    [InlineData(500, 340)]
    public void Terminal_panel_width_stays_within_the_approved_range(double portraitWidth, double expected)
    {
        Assert.Equal(expected, AttachedPanelVisualMetrics.CalculateWidth(portraitWidth));
    }

    [Fact]
    public void Custom_focus_receives_more_height_than_builtin_focus()
    {
        var builtin = AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.ExpandedFocus, false, false, 900);
        var custom = AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.ExpandedFocus, false, true, 900);

        Assert.Equal(240, builtin);
        Assert.Equal(370, custom);
    }

    [Fact]
    public void Compact_timer_and_message_have_explicit_minimum_budgets()
    {
        Assert.Equal(150, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900));
        Assert.Equal(170, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 900));
    }

    [Fact]
    public void Every_height_is_capped_to_sixty_percent_of_the_work_area()
    {
        Assert.Equal(180, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.ExpandedFocus, false, true, 300));
    }
}
