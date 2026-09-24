using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class CompactionPlannerTests
{
    [Fact]
    public void Trigger_reserves_output_before_applying_window_ratio()
    {
        var budget = PromptBudget.Resolve(new(new("test", "endpoint", "m", "1"), 32768, null, ContextLimitSource.Override, "fixture"), 4096);
        Assert.Equal(26214, CompactionPlanner.TriggerTokens(budget));
        var constrained = PromptBudget.Resolve(new(new("test", "endpoint", "m", "1"), 32768, null, ContextLimitSource.Override, "fixture"), 10000);
        Assert.Equal(constrained.InputTokens, CompactionPlanner.TriggerTokens(constrained));
    }

    [Fact]
    public void Recent_exchanges_and_incomplete_group_are_not_cut()
    {
        CompactionTurn[] turns = [new(1, 2, 1000, true), new(3, 4, 1000, true), new(5, 6, 1000, true), new(7, 7, 200, false)];
        Assert.Equal(new CompactableRange(1, 2), CompactionPlanner.Select(turns, 1000));
        Assert.Null(CompactionPlanner.Select(turns, 10000));
    }

    [Fact]
    public void Only_two_groups_or_incomplete_prefix_cannot_be_compacted()
    {
        Assert.Null(CompactionPlanner.Select([new(1, 2, 100000, true), new(3, 4, 1, true)], 10));
        Assert.Null(CompactionPlanner.Select([new(1, 1, 1, false), new(2, 3, 100, true), new(4, 5, 100, true), new(6, 7, 100, true)], 10));
    }

    [Fact]
    public void Tool_proposal_and_its_reply_are_retained_as_one_complete_unit()
    {
        // DeepSeek region pairing fixture: no persisted generic tool role is introduced.
        CompactionTurn[] turns = [new(1, 3, 200, true), new(4, 6, 200, true), new(7, 9, 200, true)];
        Assert.Equal(new CompactableRange(1, 3), CompactionPlanner.Select(turns, 200));
    }
}
