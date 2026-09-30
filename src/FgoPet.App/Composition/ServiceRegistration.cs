using FgoPet.Core.Panels;
using FgoPet.Infrastructure.Speech;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.App.Theming;
using FgoPet.App.Memory;
using FgoPet.App.Main;
using FgoPet.App.Focus;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
using FgoPet.App.Lifetime;
using FgoPet.App.Panels;
using FgoPet.App.Portraits;
using FgoPet.App.Portraits.Live2D;
using FgoPet.App.Runtime;
using FgoPet.App.Privacy;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Tray;
using FgoPet.App.Services;
using FgoPet.App.Speech;
using FgoPet.App.ViewModels;
using FgoPet.App.Archives;
using FgoPet.App.Views.Settings;
using FgoPet.App.Windowing;
using FgoPet.Core.Agents;
using FgoPet.Core.Archives;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Windowing;
using FgoPet.Character.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Agents;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Infrastructure.Settings;
using FgoPet.Infrastructure.Windowing;
using FgoPet.Memory.Settings;
using FgoPet.Platform.Settings;
using FgoPet.SettingsHost;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using Microsoft.Extensions.Logging;
using FgoPet.Kernel.Lifecycle;
using FgoPet.Kernel.Conversation;
using FgoPet.Plugin.Todo.Desktop;
using FgoPet.Plugin.Focus.Desktop;
using FgoPet.Plugin.Memory.Desktop;
using FgoPet.Kernel.Companion;
using FgoPet.UiSdk;
using FgoPet.Extensibility;
using FgoPet.Plugin.Content;
using FgoPet.Character.Presentation;

namespace FgoPet.App.Bootstrap;

public static class ServiceRegistration
{
    public static IServiceCollection AddFgoPet(this IServiceCollection services, string[] args, Func<Task>? normalExit = null, bool includeTodo = true, bool includeFocus = true, bool includeMemory = true, bool includeSpeech = true, bool includeIndexTts = true, bool includeAgentBackend = true)
    {
        var paths = AppPaths.ForCurrentUser();
        if (includeSpeech) services.AddBuiltInSpeechProviders();
        if (includeSpeech && includeIndexTts) services.AddIndexTtsProvider();
        services
        .AddAgentBackendServices(paths.StorageRoot, includeAgentBackend)
        .AddTodoCapability(includeTodo, paths.StorageRoot)
        .AddFocusCapability(includeFocus)
        .AddMemoryCapability(includeMemory, provider => new ProviderMemoryCandidateExtractor(
            connection => provider.GetRequiredService<IChatProviderResolver>().Resolve(connection),
            provider.GetRequiredService<IModelContextResolver>(), provider.GetRequiredService<IRequestTokenMeter>(),
            provider.GetRequiredService<IMemoryRecall>()),
            provider => () => provider.GetRequiredService<IDialogueSettingsStore>().Load().ModelConnection)
        .AddSingleton(TimeProvider.System)
        .AddLogging(builder => builder.AddDebug())
        .AddSingleton<TextWriter>(Console.Out)
        .AddSingleton(paths)
        .AddSingleton<ProcessLifetime>()
        .AddSingleton<IProcessLifetime>(provider => provider.GetRequiredService<ProcessLifetime>())
        .AddSingleton<BackgroundOperationTracker>()
        .AddSingleton<IAppLifetime>(_ => new AppLifetimeService(Application.Current!, normalExit))
        .AddSingleton<ISettingsDocumentStore>(_ => new JsonSettingsDocumentStore(paths.StorageRoot))
        .AddSingleton<ApplicationSettingsCoordinator>()
        .AddSingleton<IApplicationSettingsDocument>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<ICharacterSettingsStore>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<IDialogueSettingsStore>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<IMemorySettingsStore>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<IWorkExecutionSettingsStore>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<ISpeechSettingsStore>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<IThemeSettingsStore>(provider => provider.GetRequiredService<ApplicationSettingsCoordinator>())
        .AddSingleton<ThemeService>(provider => new ThemeService(
            provider.GetRequiredService<IThemeSettingsStore>(),
            Application.Current?.Resources ?? new ResourceDictionary()))
        .AddSingleton<IWindowPlacementStore>(_ => new JsonWindowPlacementStore(paths.StorageRoot))
        .AddSingleton<IScreenLayoutService, WindowsScreenLayoutService>()
        .AddContentCapability(paths.PackagesRoot, paths.StorageRoot, FgoPetAppVersion.Current)
        .AddSingleton<IExpressionResolver, ExpressionResolver>()
        .AddStaticPortraitBackend()
        .AddSingleton(provider => new Live2DPortraitController(
            provider.GetRequiredService<PortraitController>(),
            provider.GetRequiredService<IArtPackageRepository>(),
            Path.Combine(AppContext.BaseDirectory, "Live2D"),
            Path.Combine(paths.StorageRoot, "Live2DSessions"),
            Path.Combine(paths.StorageRoot, "Live2DWebView2")))
        .Replace(ServiceDescriptor.Singleton<FgoPet.Kernel.Presentation.IPortraitBackend>(provider => provider.GetRequiredService<Live2DPortraitController>()))
        .AddSingleton<IPortraitSurface>(provider => provider.GetRequiredService<FgoPet.Kernel.Presentation.IPortraitBackend>() as IPortraitSurface
            ?? throw new InvalidOperationException("The selected portrait backend has no desktop surface."))
        .AddSingleton<IPortraitController>(provider => provider.GetRequiredService<FgoPet.Kernel.Presentation.IPortraitBackend>())
        .AddSingleton<PortraitActivation>(provider => provider.GetRequiredService<RoleActivationService>().ActivatePortraitAsync)
        // One shared database; each capability owns its repositories and runtime.
        .AddSingleton(provider => new RuntimeDatabase(provider.GetRequiredService<AppPaths>().RuntimeDatabasePath))
        .AddSingleton<RuntimeDatabaseSnapshotService>(provider => new RuntimeDatabaseSnapshotService(
            provider.GetRequiredService<RuntimeDatabase>(),
            eventName => provider.GetRequiredService<ILogger<RuntimeDatabaseSnapshotService>>()
                .LogInformation("{BackupEvent}", eventName)))
        .AddSingleton<BackupPackageReferencesCodec>()
        .AddSingleton<PrivateBackupReader>()
        .AddSingleton<PrivateBackupService>(provider => new PrivateBackupService(
            provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IApplicationSettingsDocument>(),
            provider.GetRequiredService<IPackIndexStore>(),
            provider.GetRequiredService<RuntimeDatabaseSnapshotService>(),
            provider.GetRequiredService<TimeProvider>(),
            FgoPetAppVersion.Current.ToString(),
            eventName => provider.GetRequiredService<ILogger<PrivateBackupService>>()
                .LogInformation("{BackupEvent}", eventName)))
        .AddSingleton<FgoPet.App.Bootstrap.IRuntimeDatabaseMigrator>(provider =>
            new SqliteRuntimeDatabaseMigrator(provider.GetRequiredService<RuntimeDatabase>(), provider.GetRequiredService<IMemoryWriteLifetime>()))
        .AddSingleton<IPhase2Availability, Phase2Availability>()
        .AddSingleton<SqliteWorkArchiveRepository>()
        .AddSingleton<IWorkArchiveRepository>(provider => provider.GetRequiredService<SqliteWorkArchiveRepository>())
        .AddSingleton(provider => PluginCatalog.Create(provider.GetServices<IFgoPetPlugin>()))
        .AddSingleton<PluginRuntime>()
        .AddSingleton<ConversationCapabilityRouter>()
        .AddSingleton<CompanionSignalPipeline>()
        .AddSingleton<CompanionPresentation>()
        .AddSingleton<CompanionReactionPipeline>()
        .AddSingleton<IWorkspaceCatalog, WorkspaceCatalog>()
        .AddSingleton<ITransientSurfaceCatalog, TransientSurfaceCatalog>()
        .AddSingleton<ArchiveDraftService>()
        .AddSingleton<ILongArchiveSummaryStore, MemoryLongArchiveSummaryStore>()
        .AddSingleton<LongArchiveService>()
        .AddSingleton<IFocusRestorer>(provider => new FocusServiceRestorer(provider.GetService<FocusSessionService>() is { } owner ? owner.Restore : null))
        .AddSingleton<AppRuntime>()
        .AddSingleton<RoleActivationService>()
        .AddSingleton<IRoleActivationService>(provider => provider.GetRequiredService<RoleActivationService>())
        .AddSingleton<ServantPreferenceService>()
        .AddSingleton<ServantLibraryViewModel>()
        .AddSingleton<IDialogueProjectCatalog>(provider =>
            new AgentDialogueProjectCatalog(provider.GetRequiredService<IAgentTargetCatalog>()))
        .AddSingleton<IAppMaintenanceCoordinator>(provider => new AppMaintenanceCoordinator(
            provider.GetRequiredService<IAgentRelayRuntime>()))
        // Phase 3 model connection: metadata in JSON, key in Credential Manager.
        .AddSingleton<HttpClient>()
        .AddSpeechServices(includeSpeech)
        .AddSingleton<WindowsCredentialStore>()
        .AddSingleton<ICredentialStore>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<ICredentialReader>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<FgoPet.Core.Secrets.ICredentialStore>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddSingleton<FgoPet.Core.Secrets.ICredentialReader>(provider => provider.GetRequiredService<WindowsCredentialStore>())
        .AddDialogueRuntime()
        .AddModelConnectionSettings()
        .AddSpeechSettings(Path.Combine(paths.StorageRoot, "voices"), includeSpeech)
        .AddSingleton<SettingsViewModel>()
        // Modules construct settings views against ISettingsNavigator so the dependency
        // direction stays host -> module. Resolve it to the same shell instance.
        .AddSingleton<ISettingsNavigator>(provider => provider.GetRequiredService<SettingsViewModel>())
        .AddSingleton<ISettingsPageNavigator>(provider => provider.GetRequiredService<SettingsViewModel>())
        .AddSingleton<UserProfileViewModel>()
        .AddSingleton(provider => new UserProfileWebFactory(
            provider.GetRequiredService<ICharacterSettingsStore>(),
            provider.GetRequiredService<UserProfileViewModel>(), paths.StorageRoot,
            provider.GetRequiredService<ThemeService>()))
        .AddCharacterSettings()
        .AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.UserProfile), _ => provider.GetRequiredService<UserProfileWebFactory>().CreateView(),
            "用户资料", "管理全局用户资料。", "开始使用", ["用户", "资料"], 10))
        .AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            nameof(SettingsSection.Privacy), _ => provider.GetRequiredService<PrivacyPage>(),
            "数据与隐私", "导出或清理本地用户数据。", "管理", ["数据", "隐私", "导出"], 210))
        .AddSingleton(provider => new SettingsPageCatalog(provider.GetServices<ISettingsPageViewFactory>(),
            provider.GetRequiredService<IProcessLifetime>().StoppingToken))
        .AddSingleton<SettingsPageContentResolver>(provider => (section, route) =>
            provider.GetRequiredService<SettingsPageCatalog>().CreateView(section.ToString(),
                new(route?.PackageId, route?.DisplayName)) ?? new Border())
        .AddSingleton<SettingsWindow>(provider =>
        {
            provider.InitializeMemoryContext();
            return new SettingsWindow(
                provider.GetRequiredService<SettingsViewModel>(),
                provider.GetRequiredService<SettingsPageCatalog>());
        })
        .AddSingleton<UserDataExportService>()
        .AddSingleton<IUserDataExporter>(provider => provider.GetRequiredService<UserDataExportService>())
        .AddSingleton<UserDataDeletionService>()
        .AddSingleton<IUserDataDeleter>(provider => provider.GetRequiredService<UserDataDeletionService>())
        .AddSingleton<PendingBackupRestoreService>(provider => new PendingBackupRestoreService(
            paths.StorageRoot, provider.GetRequiredService<IApplicationSettingsDocument>(), provider.GetRequiredService<PrivateBackupReader>()))
        .AddSingleton<PrivateBackupRestoreService>(provider => new PrivateBackupRestoreService(
            provider.GetRequiredService<RuntimeDatabase>(),
            provider.GetRequiredService<IApplicationSettingsDocument>(),
            provider.GetRequiredService<IPackIndexStore>(),
            provider.GetRequiredService<PrivateBackupService>(),
            provider.GetRequiredService<PrivateBackupReader>(),
            provider.GetRequiredService<IAppMaintenanceCoordinator>(),
            paths.StorageRoot,
            provider.GetRequiredService<TimeProvider>(),
            packageRepository: provider.GetRequiredService<IArtPackageRepository>(),
            safeLog: eventName => provider.GetRequiredService<ILogger<PrivateBackupRestoreService>>()
                .LogInformation("{BackupEvent}", eventName)))
        .AddSingleton<PrivacyPage>(provider => new PrivacyPage(
            provider.GetRequiredService<MemoryViewModel>(),
            provider.GetService<PrivateBackupService>(),
            provider.GetService<PendingBackupRestoreService>(),
            provider.GetRequiredService<IAppLifetime>().RequestNormalExit))
        .AddSingleton<DialogueWindowViewModel>(provider => new DialogueWindowViewModel(
            provider.GetRequiredService<ConversationViewModel>(),
            provider.GetRequiredService<ServantLibraryViewModel>(),
            provider.GetRequiredService<SpeechPlaybackCoordinator>(),
            provider.GetRequiredService<IDialogueProjectCatalog>()))
        .AddSingleton<DialogueWindow>()
        .AddSingleton<DialogueWindowPlacementCoordinator>()
        .AddSingleton(provider =>
        {
            var conversation = provider.GetRequiredService<ConversationViewModel>();
            return new AttachedPanelViewModel(
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<ICompactSurface>(),
                conversation,
                provider.GetRequiredService<AppRuntime>(),
                provider.GetRequiredService<DialogueWindowViewModel>(),
                provider.GetRequiredService<ISpeechSettingsStore>(),
                provider.GetRequiredService<CompanionPresentation>(),
                surfaceId => provider.GetRequiredService<TransientSurfaceCoordinator>().Toggle(surfaceId));
        })
        .AddSingleton<IAttachedPanelLauncher>(provider => provider.GetRequiredService<AttachedPanelViewModel>())
        .AddSingleton(provider => new PortraitWindow(
            provider.GetRequiredService<AttachedPanelViewModel>()))
        .AddSingleton<PortraitWindowCoordinator>()
        .AddSingleton(provider => new TransientSurfaceCoordinator(
            provider.GetRequiredService<ITransientSurfaceCatalog>(),
            provider.GetRequiredService<IScreenLayoutService>(),
            () => provider.GetRequiredService<PortraitWindow>(),
            () => provider.GetRequiredService<PortraitWindowCoordinator>()))
        .AddSingleton<IWorkspaceLauncher>(provider => new WorkspaceWindowCoordinator(
            provider.GetRequiredService<IWorkspaceCatalog>(),
            () => provider.GetRequiredService<PortraitWindow>()))
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
            provider.GetRequiredService<IPortraitSurface>(),
            provider.GetRequiredService<ConversationViewModel>(),
            provider.GetRequiredService<PortraitActivation>(),
            provider.GetRequiredService<ICharacterSettingsStore>(),
            provider.GetRequiredService<DialogueWindow>(),
            provider.GetRequiredService<DialogueWindowViewModel>(),
            provider.GetRequiredService<AttachedPanelViewModel>(),
            provider.GetRequiredService<DialogueWindowPlacementCoordinator>()))
        .AddSingleton<IDesktopAppUi>(provider => provider.GetRequiredService<DesktopAppUi>())
        .AddSingleton<IAppShell, DesktopAppShell>()
        .AddSingleton<Func<IAppShell>>(provider => provider.GetRequiredService<IAppShell>)
        .AddSingleton<AppStartup>();
        return services;
    }

}
