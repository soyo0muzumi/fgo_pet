using FgoPet.App.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.FileSystem;
using FgoPet.Infrastructure.Packs;
using Microsoft.Extensions.DependencyInjection;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Content;

public sealed class ContentPlugin(PackageCompanionFeedbackProvider feedback) : IFgoPetPlugin, IDisposable
{
    public PluginManifest Manifest { get; } = new("firstparty.content", "1.0.0", 1, []);
    public PluginContributions Contributions { get; } = PluginContributions.Empty with { FeedbackProviders = [feedback] };
    public ValueTask StartAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}

public static class ContentRegistration
{
    public static IServiceCollection AddContentCapability(this IServiceCollection services,
        string packagesRoot, string storageRoot, SemVersion appVersion)
    {
        return services
        .AddSingleton<IPackIndexStore>(_ => new JsonPackIndexStore(storageRoot))
        .AddSingleton<IArtPackageRepository>(provider => new FileArtPackageRepository(
            packagesRoot,
            provider.GetRequiredService<IPackIndexStore>()))
        .AddSingleton<IAtomicDirectoryMover, AtomicDirectoryMover>()
        .AddSingleton<IPackInstaller>(provider => new FgoPetPackInstaller(
            PackArchivePolicy.Production,
            packagesRoot,
            storageRoot,
            appVersion,
            provider.GetRequiredService<IAtomicDirectoryMover>()))
        .AddSingleton<PackageCompanionFeedbackProvider>()
        .AddSingleton<IFgoPetPlugin, ContentPlugin>()
        .AddSingleton<IConversationContentResolver, InstalledContentBindingResolver>();
    }
}
