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
    public static IServiceCollection AddUnderstandingCapability(this IServiceCollection services,
        Func<IServiceProvider, ConversationScope, UnderstandingPagePresentation>? presentation = null)
    {
        services.TryAddSingleton(p=>new DailyReviewLedger(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(p.GetRequiredService<RuntimeDatabase>().DatabasePath)!,"understanding-daily-budget.db"),p.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<DailyReviewBudgetAccess>();
        services.TryAddSingleton<IReviewBudgetAccess>(p=>p.GetRequiredService<DailyReviewBudgetAccess>());
        services.AddSingleton(provider => new UnderstandingService(provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IExperienceTimeline>(), provider.GetRequiredService<TimeProvider>(),
            diagnostics: d => provider.GetRequiredService<ILogger<UnderstandingService>>().LogInformation(
                "Understanding {At} {Correlation} {Stage} {Code}", d.At, d.Correlation, d.Stage, d.Code)))
            .AddSingleton<IDerivedConversationData>(p => p.GetRequiredService<UnderstandingService>())
            .AddSingleton<IUnderstandingReader>(p => p.GetRequiredService<UnderstandingService>())
            .AddSingleton<IReviewModelFactory>(p=>new ConfiguredReviewModelFactory(p.GetRequiredService<IDialogueSettingsStore>(),
                p.GetRequiredService<IChatProviderResolver>(),p.GetRequiredService<IRequestTokenMeter>(),p.GetRequiredService<IReviewBudgetAccess>(),
                d=>p.GetRequiredService<ILogger<ConfiguredReviewModelFactory>>().LogInformation("Understanding {At} {Correlation} {Stage} {Code}",d.At,d.Correlation,d.Stage,d.Code),
                p.GetRequiredService<IReviewBudgetAccess>() is DailyReviewBudgetAccess ? p.GetRequiredService<IModelContextResolver>() : null))
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
                p.GetRequiredService<IDialogueContextLifetime>(), p.GetRequiredService<IExperienceTimeline>(),
                scope =>
                {
                    var display = presentation?.Invoke(p, scope) ?? new UnderstandingPagePresentation(ProjectName:
                        scope.ProjectId is null ? "Across this companion's conversations" : "Current project");
                    var connection = p.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection;
                    var recipient = connection is not null && Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var endpoint)
                        ? $"{connection.ModelId} via {endpoint.Host}:{endpoint.Port} ({connection.ProviderId})"
                        : "No model configured";
                    if (connection?.ProviderId == "custom-openai-compatible" && connection.ModelId == "buddy/deepseek-v4.1-flash" &&
                        Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var buddy) && buddy.Host == "127.0.0.1" && buddy.Port == 8326 &&
                        buddy.AbsolutePath.TrimEnd('/') == "/v1")
                        recipient += " → Tencent CodeBuddy CN; local forwarding may write or re-encode attachments";
                    return display with { ModelRecipient = recipient, AutomaticReviewAvailable = true };
                },p.GetRequiredService<DailyReviewBudgetAccess>(),p.GetRequiredService<UnderstandingReviewService>()));
        return services;
    }
}
