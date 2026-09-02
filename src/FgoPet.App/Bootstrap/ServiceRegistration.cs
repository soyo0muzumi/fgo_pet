using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FgoPet.AgentRuntime;
using FgoPet.App.Dialogue;
using FgoPet.App.Providers;
using FgoPet.App.Focus;
using FgoPet.App.Feedback;
using FgoPet.App.Lifetime;
using FgoPet.App.Main;
using FgoPet.App.Memory;
using FgoPet.App.Panels;
using FgoPet.App.Portraits;
using FgoPet.App.Privacy;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Tray;
using FgoPet.App.Theming;
using FgoPet.App.Services;
using FgoPet.App.ViewModels;
using FgoPet.App.Archives;
using FgoPet.App.Views.Settings;
using FgoPet.App.Windowing;
using FgoPet.Core.Bond;
using FgoPet.Core.Agents;
using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Bond;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Agents;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.FileSystem;
using FgoPet.Infrastructure.Focus;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Infrastructure.Settings;
using FgoPet.Infrastructure.Timeline;
using FgoPet.Infrastructure.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

public static class ServiceRegistration
{
    public static IServiceCollection AddFgoPet(this IServiceCollection services, string[] args)
    {
        var paths = AppPaths.ForCurrentUser();
        return services
        .AddSingleton(TimeProvider.System)
        .AddLogging(builder => builder.AddDebug())
        .AddSingleton<TextWriter>(Console.Out)
        .AddSingleton(paths)
        .AddSingleton<IAppLifetime>(_ => new AppLifetimeService(Application.Current!))
        .AddSingleton<IAppSettingsStore>(_ => new JsonAppSettingsStore(paths.StorageRoot))
        .AddSingleton<ThemeService>(provider => new ThemeService(
            provider.GetRequiredService<IAppSettingsStore>(),
            Application.Current?.Resources ?? new ResourceDictionary()))
        .AddSingleton<IWindowPlacementStore>(_ => new JsonWindowPlacementStore(paths.StorageRoot))
        .AddSingleton<IScreenLayoutService, WindowsScreenLayoutService>()
        .AddSingleton<IPackIndexStore>(_ => new JsonPackIndexStore(paths.StorageRoot))
        .AddSingleton<IArtPackageRepository>(provider => new FileArtPackageRepository(
            paths.PackagesRoot,
            provider.GetRequiredService<IPackIndexStore>()))
        .AddSingleton<IAtomicDirectoryMover, AtomicDirectoryMover>()
        .AddSingleton<IPackInstaller>(provider => new FgoPetPackInstaller(
            PackArchivePolicy.Production,
            paths.PackagesRoot,
            paths.StorageRoot,
            FgoPetAppVersion.Current,
            provider.GetRequiredService<IAtomicDirectoryMover>()))
        .AddSingleton<IExpressionResolver, ExpressionResolver>()
        .AddSingleton<PortraitSnapshotCache>()
        .AddSingleton(provider => new PortraitController(
            provider.GetRequiredService<IArtPackageRepository>(),
            provider.GetRequiredService<IExpressionResolver>(),
            provider.GetRequiredService<PortraitSnapshotCache>(),
            new Dpi2(1, 1)))
        .AddSingleton<IPortraitController>(provider => provider.GetRequiredService<PortraitController>())
        .AddSingleton<PortraitActivation>(provider => provider.GetRequiredService<PortraitController>().ActivateAsync)
        // Phase 2 runtime: one versioned database plus focus orchestration.
        .AddSingleton(provider => new RuntimeDatabase(provider.GetRequiredService<AppPaths>().RuntimeDatabasePath))
        .AddSingleton<RuntimeDatabaseSnapshotService>(provider => new RuntimeDatabaseSnapshotService(
            provider.GetRequiredService<RuntimeDatabase>(),
            eventName => provider.GetRequiredService<ILogger<RuntimeDatabaseSnapshotService>>()
                .LogInformation("{BackupEvent}", eventName)))
        .AddSingleton<AppSettingsSnapshotCodec>()
        .AddSingleton<BackupPackageReferencesCodec>()
        .AddSingleton<PrivateBackupReader>()
        .AddSingleton<PrivateBackupService>(provider => new PrivateBackupService(
            provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IPackIndexStore>(),
            provider.GetRequiredService<RuntimeDatabaseSnapshotService>(),
            provider.GetRequiredService<AppSettingsSnapshotCodec>(),
            provider.GetRequiredService<TimeProvider>(),
            FgoPetAppVersion.Current.ToString(),
            eventName => provider.GetRequiredService<ILogger<PrivateBackupService>>()
                .LogInformation("{BackupEvent}", eventName)))
        .AddSingleton<FgoPet.App.Bootstrap.IRuntimeDatabaseMigrator>(provider =>
            new SqliteRuntimeDatabaseMigrator(provider.GetRequiredService<RuntimeDatabase>()))
        .AddSingleton<IPhase2Availability, Phase2Availability>()
        .AddSingleton<IBondProgressionPolicy, DefaultBondProgressionPolicy>()
        .AddSingleton<SqliteFocusRepository>()
        .AddSingleton<SqliteEventStore>()
        .AddSingleton<SqliteTimelineRepository>()
        .AddSingleton<SqliteBondRepository>()
        .AddSingleton<SqliteTodoRepository>()
        .AddSingleton<SqliteAgentRepository>()
        .AddSingleton<SqliteWorkArchiveRepository>()
        .AddSingleton<ITodoRepository>(provider => provider.GetRequiredService<SqliteTodoRepository>())
        .AddSingleton<IAgentRepository>(provider => provider.GetRequiredService<SqliteAgentRepository>())
        .AddSingleton<IWorkArchiveRepository>(provider => provider.GetRequiredService<SqliteWorkArchiveRepository>())
        .AddSingleton<TodoApplicationService>()
        .AddSingleton<TodoProposalService>()
        .AddSingleton<ArchiveDraftService>()
        .AddSingleton<ILongArchiveSummaryStore, MemoryLongArchiveSummaryStore>()
        .AddSingleton<LongArchiveService>()
        .AddSingleton<DataClearService>()
        .AddSingleton<AgentDispatchService>(provider =>
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            return new AgentDispatchService(
                provider.GetRequiredService<ITodoRepository>(),
                provider.GetRequiredService<IAgentRepository>(),
                provider.GetRequiredService<IAgentGateway>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IAgentRelayAdministration>(),
                provider.GetRequiredService<AgentEventProjector>(),
                action => dispatcher.InvokeAsync(action, DispatcherPriority.DataBind).Task);
        })
        .AddSingleton<TodoListViewModel>()
        .AddSingleton<SqliteFocusCompletionUnit>()
        .AddSingleton<IFocusSnapshotStore>(provider => new SqliteFocusSnapshotStore(provider.GetRequiredService<SqliteFocusRepository>()))
        .AddSingleton<FocusSessionService>()
        .AddSingleton<IFocusSessionService>(provider => provider.GetRequiredService<FocusSessionService>())
        .AddSingleton<IFocusRestorer>(provider => new FocusServiceRestorer(provider.GetRequiredService<FocusSessionService>()))
        .AddSingleton<EventFeedbackSelector>()
        .AddSingleton<ServantFocusConnector>()
        .AddSingleton<ServantPreferenceService>()
        .AddSingleton<ServantLibraryViewModel>()
        .AddSingleton(_ =>
        {
            var defaults = RelayRuntimeOptions.ForCurrentUser();
            return new RelayRuntimeOptions(Environment.GetEnvironmentVariable("FGO_PET_PIPE_SUFFIX") ?? defaults.PipeSuffix,
                paths.StorageRoot, defaults.RelayExecutablePath, defaults.ConnectTimeout, defaults.StartupTimeout);
        })
        .AddSingleton<CodexWorkerProcess>()
        .AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<RelayRuntimeOptions>();
            return new AgentControlClient(RelayPipeNames.ForCurrentUser(options).App, options.ConnectTimeout);
        })
        .AddSingleton<AgentRelayClient>()
        .AddSingleton<IAgentGateway>(provider => provider.GetRequiredService<AgentRelayClient>())
        .AddSingleton<IAgentRelayAdministration>(provider =>
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            return new AgentRelayAdministration(
                provider.GetRequiredService<AgentControlClient>(),
                provider.GetRequiredService<IAgentRepository>(),
                provider.GetRequiredService<AgentEventProjector>(),
                action => dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task);
        })
        .AddSingleton<AgentArchiveService>(provider => new AgentArchiveService(
            provider.GetRequiredService<IAgentRepository>(),
            provider.GetRequiredService<IAgentRelayAdministration>(),
            provider.GetRequiredService<TimeProvider>()))
        .AddSingleton<AgentReconciliationService>(provider => new AgentReconciliationService(
            provider.GetRequiredService<IAgentRepository>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<AgentEventProjector>()))
        .AddSingleton<IAgentRelayRuntime>(provider =>
        {
            var options = provider.GetRequiredService<RelayRuntimeOptions>();
            var bootstrap = new RelayProcessBootstrapper(new DefaultRelayProbe(), new DefaultRelayProcessLauncher(), new DefaultRuntimeDelay());
            var projector = provider.GetRequiredService<AgentEventProjector>();
            var worker = provider.GetRequiredService<CodexWorkerProcess>();
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            return new AgentRelayRuntime(provider.GetRequiredService<AgentRelayClient>(),
                provider.GetRequiredService<IAgentRelayAdministration>(),
                async token =>
                {
                    var result = await bootstrap.EnsureReadyAsync(options, token).ConfigureAwait(false);
                    if (result.Status == RelayBootstrapStatus.Ready) worker.EnsureStarted();
                    return result;
                },
                (events, token) => dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var agentEvent in events) projector.Apply(agentEvent);
                }, DispatcherPriority.Background, token).Task);
        })
        .AddSingleton<AgentEventProjector>()
        .AddSingleton<AgentCurrentTaskViewModel>(provider => new AgentCurrentTaskViewModel(
            provider.GetRequiredService<AgentEventProjector>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<AgentReconciliationService>()))
        .AddSingleton<AgentConnectionSettingsViewModel>(provider => new AgentConnectionSettingsViewModel(
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IAgentRepository>(),
            provider.GetRequiredService<DataClearService>(),
            provider.GetRequiredService<IAgentGateway>(),
            provider.GetRequiredService<IAgentRelayAdministration>(),
            provider.GetRequiredService<IAgentRelayRuntime>(),
            provider.GetRequiredService<AgentArchiveService>()))
        .AddSingleton<AgentConnectionSettingsView>(provider => new AgentConnectionSettingsView(provider.GetRequiredService<AgentConnectionSettingsViewModel>()))
        .AddSingleton<AgentReconnectService>(provider =>
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            return new AgentReconnectService(
                provider.GetRequiredService<IAgentGateway>(),
                provider.GetRequiredService<IAgentRepository>(),
                provider.GetRequiredService<AgentEventProjector>(),
                action => dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task);
        })
        .AddSingleton<IAgentTaskLauncher, CodexTaskLauncher>()
        .AddSingleton<AgentTaskNavigationService>()
        .AddSingleton<IAppMaintenanceCoordinator>(provider => new AppMaintenanceCoordinator(
            provider.GetRequiredService<IAgentRelayRuntime>()))
        // Phase 3 model connection: metadata in JSON, key in Credential Manager.
        .AddSingleton<ProviderCatalog>()
        .AddSingleton<HttpClient>()
        .AddSingleton<WindowsCredentialStore>()
        .AddSingleton<ICredentialStore>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<ICredentialReader>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<ChatProviderFactory>()
        .AddSingleton<ModelConnectionViewModel>()
        .AddSingleton<ModelConnectionPage>()
        .AddSingleton<SettingsViewModel>()
        .AddSingleton<UserProfileViewModel>()
        .AddSingleton<UserProfilePage>()
        .AddSingleton<PersonalizationViewModel>()
        .AddSingleton<PersonalizationPage>()
        .AddSingleton<ThemePage>()
        .AddSingleton<RolePackagesPage>()
        .AddSingleton<SettingsPageContentResolver>(provider => (section, route) => section switch
        {
            SettingsSection.UserProfile => provider.GetRequiredService<UserProfilePage>(),
            SettingsSection.Personalization => provider.GetRequiredService<PersonalizationPage>(),
            SettingsSection.RolePackages when route is null => provider.GetRequiredService<RolePackagesPage>(),
            SettingsSection.RolePackages => new RolePackageDetailPage(new RolePackageDetailViewModel(
                route!,
                provider.GetRequiredService<ServantLibraryViewModel>(),
                provider.GetRequiredService<IAppSettingsStore>(),
                provider.GetRequiredService<SettingsViewModel>())),
            SettingsSection.ModelConnection => provider.GetRequiredService<ModelConnectionPage>(),
            SettingsSection.AgentConnection => provider.GetRequiredService<AgentConnectionSettingsView>(),
            SettingsSection.ConversationMemory => provider.GetRequiredService<ConversationMemoryPage>(),
            SettingsSection.Privacy => provider.GetRequiredService<PrivacyPage>(),
            SettingsSection.Theme => provider.GetRequiredService<ThemePage>(),
            // Later page migrations keep using the same in-shell resolver contract.
            _ => new Border(),
        })
        .AddSingleton<SettingsWindow>()
        // Phase 3 dialogue: user-triggered orchestration only; no startup model call.
        .AddSingleton<ApprovedKnowledgeQuery>()
        .AddSingleton<PromptComposer>()
        .AddSingleton<SqliteConversationRepository>()
        .AddSingleton<SqliteMemoryRepository>()
        .AddSingleton<ConversationSummaryService>()
        .AddSingleton<MemoryCandidateService>()
        .AddSingleton<UserDataExportService>()
        .AddSingleton<UserDataDeletionService>()
        .AddSingleton<PrivateBackupRestoreService>(provider => new PrivateBackupRestoreService(
            provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<IPackIndexStore>(),
            provider.GetRequiredService<PrivateBackupService>(),
            provider.GetRequiredService<PrivateBackupReader>(),
            provider.GetRequiredService<IAppMaintenanceCoordinator>(),
            paths.StorageRoot,
            provider.GetRequiredService<TimeProvider>(),
            packageRepository: provider.GetRequiredService<IArtPackageRepository>(),
            safeLog: eventName => provider.GetRequiredService<ILogger<PrivateBackupRestoreService>>()
                .LogInformation("{BackupEvent}", eventName)))
        .AddSingleton<MemoryViewModel>()
        .AddSingleton<ConversationMemoryPage>()
        .AddSingleton<PrivacyPage>(provider => new PrivacyPage(
            provider.GetRequiredService<MemoryViewModel>(),
            provider.GetService<PrivateBackupService>(),
            provider.GetService<PrivateBackupRestoreService>()))
        .AddSingleton<IChatProviderResolver, ConfiguredChatProviderResolver>()
        .AddSingleton<IConversationContentResolver, InstalledContentBindingResolver>()
        .AddSingleton(provider => new ConversationOrchestrator(
            provider.GetRequiredService<IChatProviderResolver>(),
            provider.GetRequiredService<IConversationContentResolver>(),
            provider.GetRequiredService<SqliteConversationRepository>(),
            provider.GetRequiredService<SqliteMemoryRepository>(),
            provider.GetRequiredService<PromptComposer>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<ConversationSummaryService>(),
            provider.GetRequiredService<TodoProposalService>()))
        .AddSingleton(provider => new ConversationViewModel(
            provider.GetRequiredService<ConversationOrchestrator>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<ModelConnectionViewModel>(),
            provider.GetRequiredService<TodoProposalService>(),
            provider.GetRequiredService<ArchiveDraftService>()))
        .AddSingleton(provider =>
        {
            var currentAgentTask = provider.GetRequiredService<AgentCurrentTaskViewModel>();
            var conversation = provider.GetRequiredService<ConversationViewModel>();
            var todoService = provider.GetRequiredService<TodoApplicationService>();
            var agentRepository = provider.GetRequiredService<IAgentRepository>();
            var archiveDrafts = provider.GetRequiredService<ArchiveDraftService>();
            currentAgentTask.OpenTaskRequested += projection =>
            {
                _ = provider.GetRequiredService<AgentTaskNavigationService>().OpenAsync(projection);
            };
            currentAgentTask.ArchiveRequested += projection =>
            {
                var coveredTodos = projection.CoveredTaskKeys
                    .Select(ParseTaskIdentity)
                    .Where(identity => identity is not null)
                    .Select(identity => agentRepository.GetExecution(identity!.Value.SourceType, identity.Value.SourceInstance, identity.Value.TaskId))
                    .Where(execution => execution is not null)
                    .Select(execution => todoService.Get(execution!.TodoId))
                    .Where(todo => todo?.Status == TodoStatus.Completed)
                    .Cast<TodoItem>()
                    .DistinctBy(todo => todo.Id)
                    .ToArray();
                if (coveredTodos.Length > 0)
                {
                    conversation.ShowArchiveDraft(archiveDrafts.CreateDraft(
                        projection.SourceType,
                        coveredTodos,
                        "Agent 目标已完成，可整理相关工作。"));
                }
            };
            return new AttachedPanelViewModel(
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IFocusSessionService>(),
                conversation,
                provider.GetRequiredService<TodoListViewModel>(),
                currentAgentTask);
        })
        .AddSingleton(provider => new PortraitWindow(
            provider.GetRequiredService<AttachedPanelViewModel>(),
            provider.GetRequiredService<IFocusSessionService>()))
        .AddSingleton<PortraitWindowCoordinator>()
        .AddSingleton<TrayService>()
        .AddSingleton(provider => new DesktopAppUi(
            provider.GetRequiredService<TrayService>(),
            provider.GetRequiredService<ServantLibraryViewModel>(),
            provider.GetRequiredService<SettingsWindow>(),
            provider.GetRequiredService<SettingsViewModel>(),
            provider.GetRequiredService<PortraitWindow>(),
            provider.GetRequiredService<PortraitWindowCoordinator>(),
            provider.GetRequiredService<IAppLifetime>(),
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<PortraitController>(),
            provider.GetRequiredService<ConversationViewModel>(),
            provider.GetRequiredService<PortraitActivation>(),
            provider.GetRequiredService<IAppSettingsStore>()))
        .AddSingleton<IDesktopAppUi>(provider => provider.GetRequiredService<DesktopAppUi>())
        .AddSingleton<IAppShell, DesktopAppShell>()
        .AddSingleton<Func<IAppShell>>(provider => provider.GetRequiredService<IAppShell>)
        .AddSingleton<Func<ServantFocusConnector>>(provider => provider.GetRequiredService<ServantFocusConnector>)
        .AddSingleton<AppStartup>();
    }

    private static (string SourceType, string SourceInstance, string TaskId)? ParseTaskIdentity(string key)
    {
        var parts = key.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : null;
    }
}
