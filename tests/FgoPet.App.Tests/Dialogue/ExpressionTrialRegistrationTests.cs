using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ExpressionTrialRegistrationTests
{
    private static readonly ModelRouteKey Route = new("test", "endpoint", "model", "1");
    private static readonly PromptBudget Budget = PromptBudget.Resolve(
        new(Route, 32768, null, ContextLimitSource.Override, "test"), 2048);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("true")]
    [InlineData("base")]
    [InlineData("legacy")]
    [InlineData("full-invalid")]
    public void Missing_or_unrecognized_session_setting_preserves_current_prompt(string? setting)
    {
        using var services = Services();
        var context = Context("mash_kyrielight", "official.mash");
        var baseline = new PromptComposer().Compose(context, Route, Budget);
        var trial = ModelConnectionRegistration.CreateTrialPromptComposer(services, setting);
        Assert.Equal(baseline.Messages, trial.Compose(context, Route, Budget).Messages);
    }

    [Theory]
    [InlineData("full")]
    [InlineData(" FULL ")]
    public void Explicit_opt_in_uses_full_v02_and_rollback_restores_baseline(string setting)
    {
        using var services = Services();
        var context = Context("mash_kyrielight", "official.mash");
        var trial = ModelConnectionRegistration.CreateTrialPromptComposer(services, setting);
        var expected = new PromptComposer(null, null, ExpressionMode.Full).Compose(context, Route, Budget);
        Assert.Equal(expected.Messages, trial.Compose(context, Route, Budget).Messages);
        Assert.Contains(expected.Messages, m => m.Text.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
        var off = ModelConnectionRegistration.CreateTrialPromptComposer(services, null);
        Assert.Equal(new PromptComposer().Compose(context, Route, Budget).Messages,
            off.Compose(context, Route, Budget).Messages);
    }

    [Theory]
    [InlineData("other", "official.mash")]
    [InlineData("mash_kyrielight", "custom.mash")]
    public void Opt_in_does_not_change_other_character_or_package(string servant, string package)
    {
        using var services = Services();
        var context = Context(servant, package);
        Assert.Equal(new PromptComposer().Compose(context, Route, Budget).Messages,
            ModelConnectionRegistration.CreateTrialPromptComposer(services, "full").Compose(context, Route, Budget).Messages);
    }

    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddSingleton<IRequestTokenMeter, RequestTokenMeter>()
        .AddSingleton<ApprovedKnowledgeQuery>().BuildServiceProvider();

    private static PromptContext Context(string servant, string package) => new(
        new(servant, package, "1", "casual", "1", "1"),
        new PersonaBundle(servant, package, "1", "1", "synthetic persona", []),
        [], [], "", [], "实验又失败了，先不展开");
}
