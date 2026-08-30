using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
using FgoPet.App.Windowing;
using FgoPet.Core.Bond;
using FgoPet.Core.Agents;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Bond;
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
        .AddSingleton<SqliteFocusCompletionUnit>()
        .AddSingleton<IFocusSnapshotStore>(provider => new SqliteFocusSnapshotStore(provider.GetRequiredService<SqliteFocusRepository>()))
        .AddSingleton<FocusSessionService>()
        .AddSingleton<IFocusSessionService>(provider => provider.GetRequiredService<FocusSessionService>())
        .AddSingleton<IFocusRestorer>(provider => new FocusServiceRestorer(provider.GetRequiredService<FocusSessionService>()))
        .AddSingleton<EventFeedbackSelector>()
        .AddSingleton<ServantFocusConnector>()
        .AddSingleton<ServantPreferenceService>()
        .AddSingleton<ServantLibraryViewModel>()
        .AddSingleton<IAgentGateway>(_ => new AgentRelayClient(string.Empty))
        .AddSingleton<AgentEventProjector>()
        .AddSingleton<AgentReconnectService>()
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
        .AddSingleton<MemoryViewModel>()
        .AddSingleton<ConversationMemoryPage>()
        .AddSingleton<PrivacyPage>()
        .AddSingleton<IChatProviderResolver, ConfiguredChatProviderResolver>()
        .AddSingleton<IConversationContentResolver, InstalledContentBindingResolver>()
        .AddSingleton<ConversationOrchestrator>()
        .AddSingleton(provider => new ConversationViewModel(
            provider.GetRequiredService<ConversationOrchestrator>(),
            provider.GetRequiredService<IAppSettingsStore>(),
            provider.GetRequiredService<ModelConnectionViewModel>()))
        .AddSingleton(provider => new AttachedPanelViewModel(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IFocusSessionService>(),
            provider.GetRequiredService<ConversationViewModel>()))
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
}
