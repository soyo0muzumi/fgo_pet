using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class PromptBudgetTests
{
    [Theory]
    [InlineData(512, 32, null, 128)]
    [InlineData(4096, 256, null, 512)]
    [InlineData(131072, 256, null, 8192)]
    [InlineData(131072, 4096, 6000, 6000)]
    [InlineData(524288, 4096, null, 16384)]
    public void Summary_policy_preserves_model_cap_and_explicit_input_constraints(int window, int reply, int? maximum, int expected)
    {
        var limit = new ModelContextLimit(new("test", "endpoint", "same", "v1"),
            window, maximum, ContextLimitSource.ProviderMetadata, "fixture");
        var primary = PromptBudget.Resolve(limit, reply);
        var summary = SummaryBudgetPolicy.Resolve(primary);
        Assert.Equal(expected, summary.OutputTokens);
        Assert.Equal(maximum, summary.ModelMaxOutputTokens);
        Assert.True(summary.InputTokens > 0);
        Assert.True(summary.InputTokens + summary.OutputTokens + summary.SafetyMarginTokens <= window);
        Assert.Equal(70, SummaryBudgetPolicy.Resolve(primary.ConstrainInput(70)).InputTokens);
        Assert.Equal(reply, primary.OutputTokens);
    }

    [Fact]
    public void Auxiliary_output_preserves_model_cap_input_constraints_and_primary_budget()
    {
        var limit = new ModelContextLimit(new("test", "endpoint", "model", "v1"),
            32768, 8192, ContextLimitSource.ProviderMetadata, "fixture");
        var primary = PromptBudget.Resolve(limit, 2048);
        var summary = primary.WithOutputReservation(8192);
        Assert.Equal(23920, summary.InputTokens);
        Assert.Equal(2048, primary.OutputTokens);
        Assert.Equal(30064, primary.InputTokens);
        Assert.Equal(16000, primary.ConstrainInput(16000).WithOutputReservation(8192).InputTokens);
        Assert.Throws<PromptBudgetException>(() => primary.WithOutputReservation(8193));
        Assert.Throws<PromptBudgetException>(() => primary.WithOutputReservation(0));
    }

    [Theory]
    [InlineData(32768, 4096, 656, 28016)]
    [InlineData(131072, 4096, 2622, 124354)]
    public void Reserves_output_and_margin_from_actual_window(int window, int output, int margin, int input)
    {
        var limit = new ModelContextLimit(new("test", "endpoint", "same", "v1"),
            window, 8192, ContextLimitSource.ProviderMetadata, "fixture");
        var budget = PromptBudget.Resolve(limit, output);
        Assert.Equal(margin, budget.SafetyMarginTokens);
        Assert.Equal(input, budget.InputTokens);
    }

    [Theory]
    [InlineData(2048, 2048, 4096)]
    [InlineData(32768, 4096, 2048)]
    [InlineData(32768, 0, 4096)]
    public void Invalid_reservations_are_rejected_before_send(int window, int output, int maxOutput)
    {
        var limit = new ModelContextLimit(new("test", "endpoint", "model", "v1"),
            window, maxOutput, ContextLimitSource.Override, "manual");
        Assert.Throws<PromptBudgetException>(() => PromptBudget.Resolve(limit, output));
    }
}
