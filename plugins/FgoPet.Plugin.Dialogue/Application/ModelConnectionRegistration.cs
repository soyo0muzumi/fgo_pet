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
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Secrets;

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
        .AddSingleton<IConversationReader>(provider => provider.GetRequiredService<SqliteConversationRepository>())
        .AddSingleton<IConversationSourceReader>(provider => new ConversationSourceReader(provider.GetRequiredService<SqliteConversationRepository>()))
        .AddSingleton<IAgentQueryResolver, ProtectedConversationQueryResolver>()
        .AddSingleton<SqliteAgentRunStore>()
        .AddSingleton<IAgentRunStore>(provider => provider.GetRequiredService<SqliteAgentRunStore>())
        .AddSingleton<IAgentFinalDeliveryStore, SqliteAgentFinalDeliveryStore>()
        .AddSingleton<NativeAgentAvailability>()
        .AddSingleton<ToolRegistry>()
        .AddSingleton<SkillRegistry>()
        .AddSingleton<AgentContextAssembler>()
        .AddSingleton(provider => new NativeConversationRuntime(provider.GetRequiredService<IAgentRunStore>(),
            provider.GetRequiredService<ToolRegistry>(), new(provider.GetRequiredService<SkillRegistry>(),
                provider.GetRequiredService<AgentContextAssembler>()), provider.GetRequiredService<TimeProvider>(),
            admissionAvailable: () => provider.GetRequiredService<NativeAgentAvailability>().IsReady))
        .AddSingleton<IFgoPetPlugin>(provider => new NativeAgentLifecyclePlugin(
            () => provider.GetRequiredService<SqliteAgentRunStore>(), () => provider.GetRequiredService<IAgentFinalDeliveryStore>(),
            () => provider.GetRequiredService<NativeConversationRuntime>(), () => provider.GetRequiredService<ConversationCapabilityRouter>(),
            provider.GetRequiredService<IConversationReader>(), provider.GetRequiredService<IDialogueSettingsStore>(),
            provider.GetRequiredService<NativeAgentAvailability>(), provider.GetRequiredService<ILogger<NativeAgentLifecyclePlugin>>()))
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
            provider.GetRequiredService<IConversationContextStore>(),
            provider.GetRequiredService<NativeConversationRuntime>(), provider.GetRequiredService<IAgentRunStore>(),
            provider.GetRequiredService<IAgentFinalDeliveryStore>(),
            scope => provider.GetService<FgoPet.App.Runtime.AppRuntime>()?.ActiveRole?.ServantId == scope.RoleId))
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
