using FgoPet.App.Main;
using FgoPet.Core.Panels;
using Xunit;

namespace FgoPet.App.Tests.Main;

public sealed class AttachedPanelVisualMetricsTests
{
    [Theory]
    [InlineData(100, 230)]
    [InlineData(220, 244)]
    [InlineData(500, 280)]
    public void Companion_panel_width_stays_within_the_approved_range(double portraitWidth, double expected)
    {
        Assert.Equal(expected, AttachedPanelVisualMetrics.CalculateWidth(portraitWidth));
    }

    [Fact]
    public void Legacy_business_states_do_not_expand_the_minimal_entry_shell()
    {
        var builtin = AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.ExpandedFocus, false, false, 900);
        var custom = AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.ExpandedFocus, false, true, 900);

        Assert.Equal(104, builtin);
        Assert.Equal(104, custom);
    }

    [Fact]
    public void Compact_timer_and_message_use_the_current_budgets()
    {
        Assert.Equal(104, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900));
        Assert.Equal(188, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 900));
        Assert.Equal(148, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900, true));
        Assert.Equal(232, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 900, true));
    }

    [Fact]
    public void Compact_quick_actions_and_timer_are_capped_by_the_work_area()
    {
        Assert.Equal(120, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 200, true));
        Assert.Equal(120, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 200, true));
    }

    [Fact]
    public void Every_height_is_capped_to_sixty_percent_of_the_work_area()
    {
        Assert.Equal(180, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 300, true));
    }
}
