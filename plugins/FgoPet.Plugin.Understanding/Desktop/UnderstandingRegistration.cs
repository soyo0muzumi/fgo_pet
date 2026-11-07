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
        services.TryAddSingleton(p=>new UnderstandingDiagnosticLog(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(p.GetRequiredService<RuntimeDatabase>().DatabasePath)!,"understanding-diagnostics.jsonl")));
        static Action<UnderstandingDiagnostic> Diagnostics<T>(IServiceProvider provider)
        {
            var file=provider.GetRequiredService<UnderstandingDiagnosticLog>();
            var logger=provider.GetRequiredService<ILogger<T>>();
            return d=>
            {
                file.Record(d);
                logger.LogInformation("Understanding {At} {Correlation} {Stage} {Code}",d.At,d.Correlation,d.Stage,d.Code);
            };
        }
        services.TryAddSingleton(p=>new DailyReviewLedger(System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(p.GetRequiredService<RuntimeDatabase>().DatabasePath)!,"understanding-daily-budget.db"),p.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(p=>new DailyReviewBudgetAccess(p.GetRequiredService<DailyReviewLedger>(),p.GetRequiredService<IDialogueSettingsStore>(),
            p.GetRequiredService<UnderstandingService>(),Diagnostics<DailyReviewBudgetAccess>(p)));
        services.TryAddSingleton<IReviewBudgetAccess>(p=>p.GetRequiredService<DailyReviewBudgetAccess>());
        services.AddSingleton(provider => new UnderstandingService(provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IExperienceTimeline>(), provider.GetRequiredService<TimeProvider>(),
            diagnostics: Diagnostics<UnderstandingService>(provider)))
            .AddSingleton<IDerivedConversationData>(p => p.GetRequiredService<UnderstandingService>())
            .AddSingleton<IUnderstandingReader>(p => p.GetRequiredService<UnderstandingService>())
            .AddSingleton<IReviewModelFactory>(p=>new ConfiguredReviewModelFactory(p.GetRequiredService<IDialogueSettingsStore>(),
                p.GetRequiredService<IChatProviderResolver>(),p.GetRequiredService<IRequestTokenMeter>(),p.GetRequiredService<IReviewBudgetAccess>(),
                Diagnostics<ConfiguredReviewModelFactory>(p),
                p.GetRequiredService<IReviewBudgetAccess>() is DailyReviewBudgetAccess ? p.GetRequiredService<IModelContextResolver>() : null))
            .AddSingleton(p=>new UnderstandingReviewService(p.GetRequiredService<UnderstandingService>(),
                p.GetRequiredService<IExperienceTimeline>(),p.GetRequiredService<IExperienceSourceReader>(),p.GetRequiredService<IReviewModelFactory>(),
                p.GetRequiredService<IDialogueContextLifetime>(),p.GetRequiredService<TimeProvider>(),Diagnostics<UnderstandingReviewService>(p),
                async (scope,token)=>
                {
                    var resolver=p.GetService<IConversationContentResolver>();
                    if(resolver is null)return null;
                    var binding=await resolver.ResolveAsync(scope.ServantId,token).ConfigureAwait(false);
                    var persona=binding.Persona;
                    if(persona is null)return null;
                    var overlay=persona.FindAppearance(binding.Context.AppearanceId);
                    return persona.CoreText+(overlay is null?"":"\n"+overlay.Text);
                },
                topicStore: p.GetService<IShortTermTopicStore>()))
            .AddSingleton(p=>new UnderstandingReviewScheduler(p.GetRequiredService<UnderstandingService>(),p.GetRequiredService<IExperienceTimeline>(),
                p.GetRequiredService<UnderstandingReviewService>(),p.GetRequiredService<TimeProvider>(),
                Diagnostics<UnderstandingReviewScheduler>(p)))
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
                },p.GetRequiredService<DailyReviewBudgetAccess>(),p.GetRequiredService<UnderstandingReviewService>(), p.GetService<ICompanionAwarenessControls>()));
        return services;
    }
}
