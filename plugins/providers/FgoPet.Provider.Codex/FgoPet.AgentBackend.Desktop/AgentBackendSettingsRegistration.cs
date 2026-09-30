using FgoPet.Infrastructure.Persistence;
using FgoPet.App.Services;
using System.Windows;
using System.Windows.Threading;
using FgoPet.AgentRuntime;
using FgoPet.Extensibility;
using FgoPet.Kernel.Lifecycle;
using FgoPet.Core.Agents;
using FgoPet.App.Views.Settings;
using FgoPet.Infrastructure.Agents;
using FgoPet.UiSdk;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.ViewModels;

public static class AgentBackendSettingsRegistration
{
    public static IServiceCollection AddAgentBackendServices(this IServiceCollection services,
        string storageRoot, bool enabled)
    {
        if (enabled)
        {
            services.AddSingleton<FgoPet.AgentBackend.AgentBackendPlugin>(provider => new(
                provider.GetRequiredService<IAgentRelayRuntime>(), provider.GetRequiredService<AgentReconnectService>(),
                () => provider.GetRequiredService<IWorkExecutionSettingsStore>().Load().AgentConnection.Enabled,
                provider.GetRequiredService<BackgroundOperationTracker>()));
            services.AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<FgoPet.AgentBackend.AgentBackendPlugin>());
        }
        return services
        .AddSingleton<SqliteAgentRepository>()
        .AddSingleton<IAgentRepository>(provider => provider.GetRequiredService<SqliteAgentRepository>())
        .AddSingleton(_ =>
        {
            var defaults = RelayRuntimeOptions.ForCurrentUser();
            return new RelayRuntimeOptions(Environment.GetEnvironmentVariable("FGO_PET_PIPE_SUFFIX") ?? defaults.PipeSuffix,
                storageRoot, defaults.RelayExecutablePath, defaults.ConnectTimeout, defaults.StartupTimeout);
        })
        .AddSingleton<IAgentTargetCatalog>(provider =>
            new CodexTargetCatalogClient(provider.GetRequiredService<RelayRuntimeOptions>()))
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
        .AddSingleton<AgentReconnectService>(provider =>
        {
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            return new AgentReconnectService(
                provider.GetRequiredService<IAgentGateway>(),
                provider.GetRequiredService<IAgentRepository>(),
                provider.GetRequiredService<AgentEventProjector>(),
                action => dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task);
        })
        .AddAgentBackendSettings(enabled);
    }

    public static IServiceCollection AddAgentBackendSettings(this IServiceCollection services, bool enabled)
    {
        if (!enabled) return services;
        services.AddSingleton(provider => new AgentConnectionSettingsViewModel(
            provider.GetRequiredService<IWorkExecutionSettingsStore>(),
            provider.GetRequiredService<IAgentRepository>(),
            provider.GetRequiredService<IAgentGateway>(),
            provider.GetRequiredService<IAgentRelayAdministration>(),
            provider.GetRequiredService<IAgentRelayRuntime>(),
            provider.GetRequiredService<IAgentTargetCatalog>()));
        services.AddSingleton<AgentConnectionSettingsView>();
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            "AgentConnection", _ => provider.GetRequiredService<AgentConnectionSettingsView>(), "Agent 连接", "连接你的 Agent，管理可访问的项目。",
            "能力", ["Agent", "连接", "项目"], 130));
        return services;
    }
}
