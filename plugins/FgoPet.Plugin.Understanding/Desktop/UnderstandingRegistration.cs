using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding.Contracts;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Settings;

namespace FgoPet.Understanding.Desktop;

public static class UnderstandingRegistration
{
    public static IServiceCollection AddUnderstandingCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IReviewBudgetAccess,UnavailableReviewBudget>();
        services.AddSingleton(provider => new UnderstandingService(provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IExperienceTimeline>(), provider.GetRequiredService<TimeProvider>(),
            diagnostics: d => provider.GetRequiredService<ILogger<UnderstandingService>>().LogInformation(
                "Understanding {At} {Correlation} {Stage} {Code}", d.At, d.Correlation, d.Stage, d.Code)))
            .AddSingleton<IDerivedConversationData>(p => p.GetRequiredService<UnderstandingService>())
            .AddSingleton<IUnderstandingReader>(p => p.GetRequiredService<UnderstandingService>())
            .AddSingleton<IReviewModelFactory>(p=>new ConfiguredReviewModelFactory(p.GetRequiredService<IDialogueSettingsStore>(),
                p.GetRequiredService<IChatProviderResolver>(),p.GetRequiredService<IRequestTokenMeter>(),p.GetRequiredService<IReviewBudgetAccess>(),
                d=>p.GetRequiredService<ILogger<ConfiguredReviewModelFactory>>().LogInformation("Understanding {At} {Correlation} {Stage} {Code}",d.At,d.Correlation,d.Stage,d.Code)))
            .AddSingleton(p=>new UnderstandingReviewService(p.GetRequiredService<UnderstandingService>(),
                p.GetRequiredService<IExperienceTimeline>(),p.GetRequiredService<IExperienceSourceReader>(),p.GetRequiredService<IReviewModelFactory>(),
                p.GetRequiredService<IDialogueContextLifetime>(),p.GetRequiredService<TimeProvider>()))
            .AddSingleton(p=>new UnderstandingReviewScheduler(p.GetRequiredService<UnderstandingService>(),p.GetRequiredService<IExperienceTimeline>(),
                p.GetRequiredService<UnderstandingReviewService>(),p.GetRequiredService<TimeProvider>(),
                d=>p.GetRequiredService<ILogger<UnderstandingReviewScheduler>>().LogInformation("Understanding {At} {Correlation} {Stage} {Code}",d.At,d.Correlation,d.Stage,d.Code)))
            .AddSingleton<IFgoPetPlugin>(p => new UnderstandingPlugin(p.GetRequiredService<IUnderstandingReader>(),
                p.GetRequiredService<IConversationScopeAuthority>().IsActive, p.GetRequiredService<IAgentQueryResolver>(),p.GetRequiredService<UnderstandingReviewScheduler>()))
            .AddSingleton<ISettingsPageViewFactory>(_ => new SettingsPageViewFactory("Understanding",
                _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"), "持续理解", "复核派生理解及其适用条件。", "能力", ["理解", "复核"], 145))
            .AddSingleton<ISettingsWebPage>(p => new UnderstandingWebPage(p.GetRequiredService<UnderstandingService>(),
                () => p.GetRequiredService<IConversationScopeAuthority>().Current,
                p.GetRequiredService<IDialogueContextLifetime>(), p.GetRequiredService<IExperienceTimeline>()));
        return services;
    }
}
