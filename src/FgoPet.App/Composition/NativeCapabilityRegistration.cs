using System.IO;
using FgoPet.App.Runtime;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Secrets;
using FgoPet.Extensibility;
using FgoPet.Platform.Processes;
using FgoPet.Platform.Secrets;
using FgoPet.Platform.Windows.Processes;
using FgoPet.Platform.Windows.Secrets;
using FgoPet.Plugin.Documents;
using FgoPet.Plugin.Browser;
using FgoPet.Plugin.DesktopContext;
using FgoPet.Plugin.Shell;
using FgoPet.Plugin.Skills;
using FgoPet.Plugin.SocialSources;
using FgoPet.Plugin.Workspace;
using FgoPet.Plugin.WebSearch;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.App.Companion;
using FgoPet.App.Dialogue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

internal static class NativeCapabilityRegistration
{
    internal static IServiceCollection AddNativeCapabilities(this IServiceCollection services) => services
        .AddSingleton<IProtectedStateProtector, WindowsStateProtector>()
        .AddSingleton<IBrowserVisualAccess>(provider => new ChromeBrowserVisualAccess(
            Path.Combine(provider.GetRequiredService<AppPaths>().StorageRoot, "chrome-browser.json")))
        .AddSingleton<IBoundedProcessDiagnostics, ProcessDiagnostics>()
        .AddSingleton<IBoundedProcessRunner, WindowsBoundedProcessRunner>()
        .AddSingleton(provider => new WorkspaceAuthorizationRegistry(scope =>
        {
            if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
            var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
            return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId;
        }))
        .AddSingleton<IWorkspaceAuthorizationSource>(provider => provider.GetRequiredService<WorkspaceAuthorizationRegistry>())
        .AddSingleton<IWorkspaceAuthorizationManager>(provider => provider.GetRequiredService<WorkspaceAuthorizationRegistry>())
        .AddSingleton<IWorkspaceAccessGuard, WorkspaceAccessGuard>()
        .AddSingleton<IFgoPetPlugin>(provider => new WorkspacePlugin(provider.GetRequiredService<IWorkspaceAccessGuard>()))
        .AddSingleton<IFgoPetPlugin>(provider => new ShellPlugin(provider.GetRequiredService<IWorkspaceAccessGuard>(),
            provider.GetRequiredService<IBoundedProcessRunner>()))
        .AddSingleton<IFgoPetPlugin>(provider => new DocumentsPlugin(provider.GetRequiredService<IWorkspaceAccessGuard>(),
            provider.GetRequiredService<IBoundedProcessRunner>(), Path.Combine(AppContext.BaseDirectory, "FgoPet.DocumentParser.exe")))
        .AddSingleton<IFgoPetPlugin, SkillsPlugin>()
        .AddSingleton<IFgoPetPlugin>(provider => new DesktopContextPlugin(
            scope => provider.GetRequiredService<IConversationScopeAuthority>().IsActive(scope),
            time: provider.GetService<TimeProvider>(),
            applicationsAuthorized: scope => provider.GetRequiredService<CompanionAwarenessHost>().ApplicationsAuthorized(scope),
            readApplications: WindowsApplicationDiscovery.Read,
            diagnostics: notification => provider.GetRequiredService<ILogger<DesktopContextPlugin>>().LogInformation(
                "Desktop context {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode),
            contextSnapshot: scope => provider.GetRequiredService<CompanionAwarenessHost>().GetSnapshot(scope)))
        .AddSingleton(provider => new WebSearchSettingsStore(
            Path.Combine(provider.GetRequiredService<AppPaths>().StorageRoot, "web-search.json"),
            Path.Combine(AppContext.BaseDirectory, "WebSearch", "FgoPet.WebSearch.exe")))
        .AddSingleton(provider => new WebSearchPlugin(
            provider.GetRequiredService<WebSearchSettingsStore>().Load,
            scope =>
            {
                if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
                var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId
                    && provider.GetRequiredService<SqliteConversationRepository>()
                        .ReadState($"LastActiveConversationId:{scope.RoleId}") == scope.ConversationId;
            },
            provider.GetRequiredService<IBoundedProcessRunner>(),
            Path.Combine(AppContext.BaseDirectory, "WebSearch", "FgoPet.WebSearch.exe"),
            notification => provider.GetRequiredService<ILogger<WebSearchPlugin>>().LogInformation(
                "Web search {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode),
            configurationError: () => provider.GetRequiredService<WebSearchSettingsStore>().GetSnapshot().ErrorCode))
        .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<WebSearchPlugin>())
        .AddSingleton(provider => new ChromeBrowserSettingsStore(
            Path.Combine(provider.GetRequiredService<AppPaths>().StorageRoot, "chrome-browser.json")))
        .AddSingleton(provider => new ChromeBrowserPlugin(
            provider.GetRequiredService<ChromeBrowserSettingsStore>().LoadEnabled,
            scope =>
            {
                if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
                var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId;
            },
            notification => provider.GetRequiredService<ILogger<ChromeBrowserPlugin>>().LogInformation(
                "Chrome browser {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode),
            imageSink: provider.GetService<IBrowserImageSink>(),
            configurationError: () => provider.GetRequiredService<ChromeBrowserSettingsStore>().Read().ErrorCode))
        .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<ChromeBrowserPlugin>())
        // Social sources: one grant file per platform, so consent for one service cannot authorize
        // another. Every platform starts disabled until an explicit local grant exists.
        .AddSingleton(provider => new SocialSourceSettingsStores(provider.GetRequiredService<AppPaths>().StorageRoot))
        .AddSingleton(provider => new SocialSourcesPlugin(
            provider.GetRequiredService<SocialSourceSettingsStores>().Bilibili.Load,
            provider.GetRequiredService<SocialSourceSettingsStores>().Xiaohongshu.Load,
            provider.GetRequiredService<SocialSourceSettingsStores>().Xiaoheihe.Load,
            () => provider.GetRequiredService<SocialSourceSettingsStores>().Bilibili.GetSnapshot().ErrorCode,
            scope =>
            {
                if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
                var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId;
            },
            notification => provider.GetRequiredService<ILogger<SocialSourcesPlugin>>().LogInformation(
                "Social source {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode),
            stores: provider.GetRequiredService<SocialSourceSettingsStores>(),
            credentials: provider.GetService<ICredentialReader>()))
        .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<SocialSourcesPlugin>());

    internal static IServiceCollection AddWebStickerCollection(this IServiceCollection services) => services
        .AddSingleton<IFgoPetPlugin>(provider => ChatWebRegistration.CreateWebStickerPlugin(
            provider.GetRequiredService<IStickerCatalogSource>(), provider.GetRequiredService<IConversationReader>(),
            provider.GetRequiredService<IConversationScopeAuthority>(), provider.GetRequiredService<WebSearchPlugin>().CaptureAuthorization,
            provider.GetRequiredService<WebSearchPlugin>().GetAvailability,
            diagnostic => provider.GetRequiredService<ILogger<WebStickerPlugin>>().LogInformation(
                "Sticker import {Timestamp} {Correlation} {Stage} {ErrorCode}", diagnostic.TimestampUtc,
                diagnostic.Correlation, diagnostic.Stage, diagnostic.ErrorCode)));

    private sealed class ProcessDiagnostics(ILogger<ProcessDiagnostics> logger) : IBoundedProcessDiagnostics
    {
        public void Record(BoundedProcessNotification notification) => logger.LogInformation(
            "Native process {Timestamp} {Correlation} {Stage} {ExitCode} {ErrorCode}", notification.Timestamp,
            notification.Correlation, notification.Stage, notification.ExitCode, notification.ErrorCode);
    }
}
