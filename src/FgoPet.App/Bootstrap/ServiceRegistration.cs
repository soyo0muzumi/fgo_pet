using System.IO;
using System.Windows;
using FgoPet.App.Lifetime;
using FgoPet.App.Main;
using FgoPet.App.Portraits;
using FgoPet.App.Servants;
using FgoPet.App.Tray;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Windowing;
using FgoPet.Infrastructure.FileSystem;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Settings;
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
        .AddSingleton<ServantLibraryViewModel>()
        .AddSingleton<ServantLibraryWindow>()
        .AddSingleton<PortraitWindow>()
        .AddSingleton<PortraitWindowCoordinator>()
        .AddSingleton<TrayService>()
        .AddSingleton<DesktopAppUi>()
        .AddSingleton<IDesktopAppUi>(provider => provider.GetRequiredService<DesktopAppUi>())
        .AddSingleton<IAppShell, DesktopAppShell>()
        .AddSingleton<Func<IAppShell>>(provider => provider.GetRequiredService<IAppShell>)
        .AddSingleton<AppStartup>();
    }
}
