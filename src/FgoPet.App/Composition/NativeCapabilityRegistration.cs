using System.IO;
using FgoPet.App.Runtime;
using FgoPet.Core.Dialogue;
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
using FgoPet.Plugin.Workspace;
using FgoPet.Plugin.WebSearch;
using FgoPet.Infrastructure.Dialogue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

internal static class NativeCapabilityRegistration
{
    internal static IServiceCollection AddNativeCapabilities(this IServiceCollection services) => services
        .AddSingleton<IProtectedStateProtector, WindowsStateProtector>()
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
        .AddSingleton<IFgoPetPlugin>(provider => new DesktopContextPlugin(scope =>
            {
                if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
                var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId;
            }, diagnostics: notification => provider.GetRequiredService<ILogger<DesktopContextPlugin>>().LogInformation(
                "Desktop context {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode)))
        .AddSingleton<IFgoPetPlugin>(provider => new WebSearchPlugin(
            () => WebSearchSettings.Load(Path.Combine(provider.GetRequiredService<AppPaths>().StorageRoot, "web-search.json")),
            scope =>
            {
                if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
                var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId
                    && provider.GetRequiredService<SqliteConversationRepository>()
                        .ReadState($"LastActiveConversationId:{scope.RoleId}") == scope.ConversationId;
            },
            token => provider.GetRequiredService<FgoPet.Core.Secrets.ICredentialReader>()
                .ReadAsync(WebSearchSettings.CredentialTarget, token),
            notification => provider.GetRequiredService<ILogger<WebSearchPlugin>>().LogInformation(
                "Web search {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode)))
        .AddSingleton<IFgoPetPlugin>(provider => new ChromeBrowserPlugin(
            () => ChromeBrowserSettings.Load(Path.Combine(provider.GetRequiredService<AppPaths>().StorageRoot, "chrome-browser.json")),
            scope =>
            {
                if (provider.GetRequiredService<AppRuntime>().ActiveRole?.ServantId != scope.RoleId) return false;
                var conversation = provider.GetRequiredService<IConversationReader>().GetConversation(scope.ConversationId, scope.RoleId);
                return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId;
            },
            notification => provider.GetRequiredService<ILogger<ChromeBrowserPlugin>>().LogInformation(
                "Chrome browser {Timestamp} {Correlation} {Stage} {ErrorCode}", notification.Timestamp,
                notification.Correlation, notification.Stage, notification.ErrorCode)));

    private sealed class ProcessDiagnostics(ILogger<ProcessDiagnostics> logger) : IBoundedProcessDiagnostics
    {
        public void Record(BoundedProcessNotification notification) => logger.LogInformation(
            "Native process {Timestamp} {Correlation} {Stage} {ExitCode} {ErrorCode}", notification.Timestamp,
            notification.Correlation, notification.Stage, notification.ExitCode, notification.ErrorCode);
    }
}
