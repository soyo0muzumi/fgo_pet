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
    public static IServiceCollection AddDialogueRuntime(this IServiceCollection services, string? diagnosticLogPath = null)
    {
        if (diagnosticLogPath is not null) services.AddSingleton(new NativeRunDiagnosticLog(diagnosticLogPath));
        return services
        .AddSingleton<SelectedImageStore>(provider => new(
            scope => provider.GetRequiredService<IConversationScopeAuthority>().IsActive(scope),
            provider.GetRequiredService<TimeProvider>(),
            () => provider.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection is { } connection
                ? ModelRouteKey.From(connection) : null))
        .AddSingleton(provider => new BrowserImageStore(provider.GetRequiredService<SelectedImageStore>(),
            scope => provider.GetRequiredService<IConversationScopeAuthority>().IsActive(scope),
            () => provider.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection is { } connection
                ? ModelRouteKey.From(connection) : null, provider.GetRequiredService<TimeProvider>()))
        .AddSingleton<IBrowserImageSink>(provider => provider.GetRequiredService<BrowserImageStore>())
        .AddSingleton<IChatImageResolver>(provider => new ConversationImageResolver(
            provider.GetRequiredService<SelectedImageStore>(), provider.GetRequiredService<BrowserImageStore>()))
        .AddSingleton<IFgoPetPlugin>(provider => new ImageToolsPlugin(
            provider.GetRequiredService<SelectedImageStore>(),
            () => provider.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection is { } connection
                ? ModelRouteKey.From(connection) : null,
            diagnostic => provider.GetRequiredService<ILogger<ImageToolsPlugin>>().LogInformation(
                "Image read {Timestamp} {Correlation} {Stage} {ErrorCode}", diagnostic.TimestampUtc,
                diagnostic.Correlation, diagnostic.Stage, diagnostic.ErrorCode)))
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
        .AddSingleton(provider => new UserInputBroker(provider.GetRequiredService<TimeProvider>()))
        .AddSingleton(provider => new ApprovalBroker(provider.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(5)))
        .AddSingleton<NativeSceneToolPolicy>()
        .AddSingleton(provider => new NativeConversationRuntime(provider.GetRequiredService<IAgentRunStore>(),
            provider.GetRequiredService<ToolRegistry>(), new(provider.GetRequiredService<SkillRegistry>(),
                provider.GetRequiredService<AgentContextAssembler>(), provider.GetRequiredService<UserInputBroker>(),
                provider.GetRequiredService<ApprovalBroker>(), ContextDiagnostics:
                    provider.GetService<NativeRunDiagnosticLog>() is { } diagnostics ? diagnostics.RecordContext : null), provider.GetRequiredService<TimeProvider>(),
            policy: provider.GetRequiredService<NativeSceneToolPolicy>(),
            admissionAvailable: () => provider.GetRequiredService<NativeAgentAvailability>().IsReady,
            diagnostics: (snapshot, stage, persisted) => provider.GetService<NativeRunDiagnosticLog>()?.Record(snapshot, stage, persisted),
            releaseAttachments: runId => provider.GetRequiredService<BrowserImageStore>().Release(runId)))
        .AddSingleton<IFgoPetPlugin>(provider => new NativeAgentLifecyclePlugin(
            () => provider.GetRequiredService<SqliteAgentRunStore>(), () => provider.GetRequiredService<IAgentFinalDeliveryStore>(),
            () => provider.GetRequiredService<NativeConversationRuntime>(), () => provider.GetRequiredService<ConversationCapabilityRouter>(),
            provider.GetRequiredService<IConversationReader>(), provider.GetRequiredService<IDialogueSettingsStore>(),
            provider.GetRequiredService<NativeAgentAvailability>(), provider.GetRequiredService<ILogger<NativeAgentLifecyclePlugin>>(),
            provider.GetRequiredService<ICompactedHistoryRepository>(),
            scope => provider.GetRequiredService<IConversationScopeAuthority>().IsActive(scope)))
        .AddSingleton<ICompactedHistoryRepository, SqliteCompactedHistoryRepository>()
        .AddSingleton<IConversationRecallRepository, SqliteConversationRecallRepository>()
        .AddSingleton<IExperienceSourceReader, SqliteExperienceSourceReader>()
        .AddSingleton<IExperienceTimeline, SqliteExperienceTimeline>()
        .AddSingleton<IConversationScopeAuthority>(provider => new ConversationScopeAuthority(
            () => provider.GetRequiredService<ConversationViewModel>(), provider.GetRequiredService<FgoPet.App.Runtime.AppRuntime>(),
            provider.GetRequiredService<SqliteConversationRepository>()))
        .AddSingleton<IFgoPetPlugin>(provider => new ExperienceToolsPlugin(
            provider.GetRequiredService<IConversationRecallRepository>(),
            provider.GetRequiredService<IExperienceSourceReader>(),
            scope =>
            {
                if (provider.GetService<FgoPet.App.Runtime.AppRuntime>()?.ActiveRole?.ServantId != scope.RoleId) return false;
                var repository = provider.GetRequiredService<SqliteConversationRepository>();
                var conversation = repository.GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId
                    && repository.ReadState($"LastActiveConversationId:{scope.RoleId}") == scope.ConversationId;
            }, provider.GetRequiredService<TimeProvider>(),
            notification => provider.GetRequiredService<ILogger<ExperienceToolsPlugin>>().LogInformation(
                "Experience read {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.TimestampUtc,
                notification.Correlation, notification.Stage, notification.ErrorCode)))
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
            provider.GetRequiredService<IConversationHistoryQuery>(), provider.GetRequiredService<TimeProvider>()));
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
