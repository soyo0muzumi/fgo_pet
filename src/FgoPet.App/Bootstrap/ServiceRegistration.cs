using System.Net.Http;
using System.IO;
using System.Windows;
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
using FgoPet.App.Windowing;
using FgoPet.Core.Bond;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.Bond;
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
        .AddSingleton<SqliteFocusCompletionUnit>()
        .AddSingleton<IFocusSnapshotStore>(provider => new SqliteFocusSnapshotStore(provider.GetRequiredService<SqliteFocusRepository>()))
        .AddSingleton<FocusSessionService>()
        .AddSingleton<IFocusSessionService>(provider => provider.GetRequiredService<FocusSessionService>())
        .AddSingleton<IFocusRestorer>(provider => new FocusServiceRestorer(provider.GetRequiredService<FocusSessionService>()))
        .AddSingleton<EventFeedbackSelector>()
        .AddSingleton<ServantFocusConnector>()
        .AddSingleton<ServantPreferenceService>()
        .AddSingleton<ServantLibraryViewModel>()
        // Phase 3 model connection: metadata in JSON, key in Credential Manager.
        .AddSingleton<ProviderCatalog>()
        .AddSingleton<HttpClient>()
        .AddSingleton<WindowsCredentialStore>()
        .AddSingleton<ICredentialStore>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<ICredentialReader>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<ChatProviderFactory>()
        .AddSingleton<ModelConnectionViewModel>()
        .AddSingleton<ModelConnectionWindow>()
        // Phase 3 dialogue: user-triggered orchestration only; no startup model call.
        .AddSingleton<PromptComposer>()
        .AddSingleton<SqliteConversationRepository>()
        .AddSingleton<SqliteMemoryRepository>()
        .AddSingleton<ConversationSummaryService>()
        .AddSingleton<MemoryCandidateService>()
        .AddSingleton<UserDataExportService>()
        .AddSingleton<UserDataDeletionService>()
        .AddSingleton<MemoryViewModel>()
        .AddSingleton<MemoryWindow>()
        .AddSingleton<IChatProviderResolver, ConfiguredChatProviderResolver>()
        .AddSingleton<IConversationContentResolver, InstalledContentBindingResolver>()
        .AddSingleton<ConversationOrchestrator>()
        .AddSingleton<ConversationViewModel>()
        .AddSingleton(provider => new AttachedPanelViewModel(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IFocusSessionService>(),
            provider.GetRequiredService<ConversationViewModel>()))
        .AddSingleton<ServantLibraryWindow>(provider => new ServantLibraryWindow(
            provider.GetRequiredService<ServantLibraryViewModel>(),
            provider.GetRequiredService<MemoryWindow>()))
        .AddSingleton(provider => new PortraitWindow(
            provider.GetRequiredService<AttachedPanelViewModel>(),
            provider.GetRequiredService<IFocusSessionService>()))
        .AddSingleton<PortraitWindowCoordinator>()
        .AddSingleton<TrayService>()
        .AddSingleton<DesktopAppUi>()
        .AddSingleton<IDesktopAppUi>(provider => provider.GetRequiredService<DesktopAppUi>())
        .AddSingleton<IAppShell, DesktopAppShell>()
        .AddSingleton<Func<IAppShell>>(provider => provider.GetRequiredService<IAppShell>)
        .AddSingleton<Func<ServantFocusConnector>>(provider => provider.GetRequiredService<ServantFocusConnector>)
        .AddSingleton<AppStartup>();
    }
}
