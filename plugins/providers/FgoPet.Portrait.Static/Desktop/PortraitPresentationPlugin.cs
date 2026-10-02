using FgoPet.Core.Packs;
using FgoPet.Core.Geometry;
using FgoPet.Core.Portraits;
using FgoPet.Kernel.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;
using FgoPet.App.Portraits;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;

namespace FgoPet.Character.Presentation;

/// <summary>Renderer-owned adapter from semantic state to the active portrait.</summary>
public sealed class PortraitPresentationPlugin(CompanionPresentation presentation, PortraitController portrait) : IFgoPetPlugin, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationToken _stopping;
    private int _started;
    private int _closed;
    public PluginManifest Manifest { get; } = new("firstparty.portrait-static", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty;
    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return ValueTask.CompletedTask;
        _stopping = stoppingToken;
        presentation.Changed += OnChanged;
        return ValueTask.CompletedTask;
    }
    private void OnChanged(CompanionPresentationState? state)
    {
        if (state is null || Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested) return;
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            var selection = portrait.CurrentState?.Selection;
            if (Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested || !presentation.IsCurrent(state)
                || selection?.PackageId != state.Role.PackageId || selection.AppearanceId != state.Role.AppearanceId
                || selection.PackageVersion != state.Role.PackageVersion) return;
            portrait.SetExpression(state.Expression);
        });
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) { Dispose(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        presentation.Changed -= OnChanged;
    }
}

public static class StaticPortraitRegistration
{
    public static IServiceCollection AddStaticPortraitBackend(this IServiceCollection services)
    {
        return services
        .AddSingleton<PortraitSnapshotCache>()
        .AddSingleton(provider => new PortraitController(
            provider.GetRequiredService<IArtPackageRepository>(),
            provider.GetRequiredService<IExpressionResolver>(),
            provider.GetRequiredService<PortraitSnapshotCache>(),
            new Dpi2(1, 1), provider.GetRequiredService<IProcessLifetime>().StoppingToken))
        .AddSingleton<FgoPet.Kernel.Presentation.IPortraitBackend>(provider => provider.GetRequiredService<PortraitController>())
        .AddSingleton<IFgoPetPlugin, PortraitPresentationPlugin>();
    }
}
