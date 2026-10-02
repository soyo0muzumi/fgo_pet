using FgoPet.Extensibility;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Conversation;
using FgoPet.Kernel.Lifecycle;
using Microsoft.Extensions.Logging;
using FgoPet.App.Settings;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Dialogue;

public static class ModelConnectionRegistration
{
    public static IServiceCollection AddDialogueRuntime(this IServiceCollection services)
    {
        return services
        .AddSingleton<ProviderCatalog>()
        .AddSingleton<ChatProviderFactory>()
        .AddSingleton<ApprovedKnowledgeQuery>()
        .AddSingleton<IRequestTokenMeter, RequestTokenMeter>()
        .AddSingleton<IModelContextResolver>(provider => new ModelContextResolver(
            connection => provider.GetRequiredService<ChatProviderFactory>().Create(connection),
            provider.GetRequiredService<TimeProvider>()))
        .AddSingleton<PromptComposer>()
        .AddSingleton(provider => new DialogueContextLifetime(provider.GetRequiredService<IProcessLifetime>().StoppingToken))
        .AddSingleton<IDialogueContextLifetime>(provider => provider.GetRequiredService<DialogueContextLifetime>())
        .AddSingleton<SqliteConversationRepository>()
        .AddSingleton<IConversationSourceReader>(provider => new ConversationSourceReader(provider.GetRequiredService<SqliteConversationRepository>()))
        .AddSingleton<IConversationRecallRepository, SqliteConversationRecallRepository>()
        .AddSingleton<IConversationRecall, ConversationRecallService>()
        .AddSingleton<IConversationContextStore, SqliteConversationContextStore>()
        .AddSingleton<IConversationSummarizer, ProviderConversationSummarizer>()
        .AddSingleton<IConversationHistoryQuery>(provider => provider.GetRequiredService<SqliteConversationRepository>())
        .AddSingleton<ConversationSummaryService>()
        .AddSingleton<IChatProviderResolver, ConfiguredChatProviderResolver>()
        .AddSingleton(provider => new ConversationOrchestrator(
            provider.GetRequiredService<IChatProviderResolver>(),
            provider.GetRequiredService<IConversationContentResolver>(),
            provider.GetRequiredService<SqliteConversationRepository>(),
            provider.GetRequiredService<PromptComposer>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IDialogueSettingsStore>(),
            provider.GetRequiredService<ConversationSummaryService>(),
            provider.GetRequiredService<ConversationCapabilityRouter>(),
            provider.GetRequiredService<ILogger<ConversationOrchestrator>>(),
            provider.GetRequiredService<IModelContextResolver>(),
            provider.GetRequiredService<IRequestTokenMeter>(),
            provider.GetRequiredService<DialogueContextLifetime>(),
            provider.GetRequiredService<IConversationRecall>(),
            provider.GetRequiredService<IConversationContextStore>()))
        .AddSingleton(provider => new ConversationViewModel(
            provider.GetRequiredService<ConversationOrchestrator>(),
            provider.GetRequiredService<IDialogueSettingsStore>(),
            provider.GetRequiredService<ModelConnectionViewModel>(),
            provider.GetRequiredService<IConversationHistoryQuery>()));
    }

    public static IServiceCollection AddModelConnectionSettings(this IServiceCollection services)
    {
        services.AddSingleton<ModelConnectionViewModel>();
        services.AddSingleton<ISettingsWebPage>(provider => new ModelConnectionWebPage(
            provider.GetRequiredService<ModelConnectionViewModel>()));
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.ModelConnection), _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"), "模型服务", "连接聊天服务，选择默认使用的模型。",
            "开始使用", ["模型", "连接", "聊天"], 30));
        return services;
    }
}
