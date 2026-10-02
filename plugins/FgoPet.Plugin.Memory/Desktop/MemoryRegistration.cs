using System.Windows.Threading;
using FgoPet.App.Runtime;
using FgoPet.App.Dialogue;
using FgoPet.App.Memory;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Memory;
using FgoPet.Memory.Settings;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.Plugin.Memory.Desktop;

public static class MemoryRegistration
{
    public static void InitializeMemoryContext(this IServiceProvider provider) =>
        provider.GetRequiredService<MemoryContextConnector>();

    public static IServiceCollection AddMemoryCapability(this IServiceCollection services, bool enabled,
        Func<IServiceProvider, IMemoryCandidateExtractor> extractorFactory,
        Func<IServiceProvider, Func<ModelConnectionSettings?>> currentConnection)
    {
        // Preserve privacy/backup access to stored data even when conversation contributions are absent.
        services.AddSingleton<SqliteMemoryRepository>()
            .AddSingleton<IMemoryCandidateSink>(provider => provider.GetRequiredService<SqliteMemoryRepository>())
            .AddSingleton<IMemoryWriteLifetime>(provider => provider.GetRequiredService<SqliteMemoryRepository>())
            .AddSingleton<IMemorySnapshotReader>(provider => provider.GetRequiredService<SqliteMemoryRepository>())
            .AddSingleton<IMemoryRecall, MemoryRecallService>()
            .AddSingleton<IMemoryCandidateExtractor>(extractorFactory)
            .AddSingleton(provider =>
            {
                var connection = currentConnection(provider);
                var settings = provider.GetRequiredService<IMemorySettingsStore>();
                var sources = new MemoryExtractionSourceReader(provider.GetRequiredService<IConversationSourceReader>());
                return new MemoryExtractionQueue(provider.GetRequiredService<IMemoryCandidateExtractor>(),
                    provider.GetRequiredService<IMemoryCandidateSink>(), provider.GetRequiredService<IMemoryWriteLifetime>(),
                    provider.GetRequiredService<DialogueContextLifetime>(),
                    captured => settings.Load().Enabled && captured == connection(),
                    provider.GetRequiredService<ILogger<MemoryExtractionQueue>>(), isSourceCurrent: sources.IsCurrent);
            })
            .AddSingleton<MemoryCandidateService>()
            .AddSingleton<MemoryViewModel>()
            .AddSingleton(provider => new MemoryContextConnector(provider.GetRequiredService<AppRuntime>(),
                provider.GetRequiredService<MemoryViewModel>(), Dispatcher.CurrentDispatcher));
        if (enabled)
        {
            services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
                nameof(SettingsSection.ConversationMemory), _ => throw new InvalidOperationException("SETTINGS_USE_WEB_ROOT"),
                "对话与记忆", "管理对话和记忆偏好。", "能力", ["对话", "记忆"], 140));
            services.AddSingleton<ISettingsWebPage>(provider => new MemoryWebPage(
                provider.GetRequiredService<MemoryViewModel>()));
            services.AddSingleton<IFgoPetPlugin>(provider => new MemoryPlugin(
                provider.GetRequiredService<IMemoryRecall>(), provider.GetRequiredService<IConversationSourceReader>(),
                provider.GetRequiredService<IMemoryCandidateSink>(), provider.GetRequiredService<MemoryExtractionQueue>(),
                () => provider.GetRequiredService<IMemorySettingsStore>().Load().Enabled));
        }
        return services;
    }
}
