using System.Text.Json;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Lifecycle;

public sealed class RunBudgetCompatibilityTests
{
    [Fact]
    public void Old_checkpoint_budget_keeps_original_policy_and_new_opt_in_round_trips()
    {
        var previous = JsonSerializer.Deserialize<RunBudget>("""{"MaxModelRequests":8,"MaxToolCalls":16,"MaxLoadedSkills":8}""");
        Assert.NotNull(previous);
        Assert.False(previous.ReserveFinalResponse);
        Assert.Equal(8, previous.MaxModelRequests);
        var current = new RunBudget(reserveFinalResponse: true);
        Assert.Equal(current, JsonSerializer.Deserialize<RunBudget>(JsonSerializer.Serialize(current)));
    }
}
