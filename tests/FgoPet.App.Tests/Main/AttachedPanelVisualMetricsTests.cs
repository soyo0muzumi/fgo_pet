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

        // 132 DIP is the compact entry shell (three 36 DIP entries plus gaps); a custom
        // preset must not add height of its own.
        Assert.Equal(132, builtin);
        Assert.Equal(132, custom);
    }

    [Fact]
    public void Compact_timer_and_message_use_the_current_budgets()
    {
        Assert.Equal(132, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900));
        Assert.Equal(188, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 900));
        Assert.Equal(312, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900, true));
        Assert.Equal(312, AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, true, false, 900, true));
    }

    [Fact]
    public void Expanded_quick_actions_budget_covers_the_entry_buttons()
    {
        // The four quick-action entries are 36 DIP each with an 8 DIP gap, so the
        // expanded budget must add at least 176 DIP over the compact entry shell.
        var compact = AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900);
        var expanded = AttachedPanelVisualMetrics.CalculateHeight(AttachedPanelState.Compact, false, false, 900, true);

        Assert.True(expanded - compact >= 4 * 44, $"expanded={expanded} compact={compact}");
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
