using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Lifecycle;

public sealed class HostRecommendationPolicyTests
{
    [Fact]
    public void Research_formatting_and_refinement_are_bounded_to_explicit_tasks()
    {
        var prompt = NativeAgentEnvironment.CorePrompt;
        Assert.Contains("For explicit recommendation or comparison tasks", prompt);
        Assert.Contains("Do not apply shortlist/report formatting to casual single-topic questions", prompt);
        Assert.Contains("Do not append unsolicited offers", prompt);
        Assert.DoesNotContain("offer refinement after the initial answer", prompt);
    }
}
