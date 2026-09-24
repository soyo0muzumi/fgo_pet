using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class PromptBudgetTests
{
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
