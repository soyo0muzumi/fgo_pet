using System.Windows.Threading;
using FgoPet.App.Focus;
using FgoPet.Core.Bond;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Bond;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.Focus;
using FgoPet.Infrastructure.Timeline;
using Microsoft.Extensions.DependencyInjection;
using FgoPet.UiSdk;
using FgoPet.App.Runtime;
using FgoPet.Kernel.Lifecycle;
using FgoPet.Plugin.Focus;

namespace FgoPet.Plugin.Focus.Desktop;

public static class FocusRegistration
{
    public static IServiceCollection AddFocusCapability(this IServiceCollection services, bool enabled = true,
        Func<IServiceProvider, Func<string?>>? factoryActiveRole = null)
    {
        // Data adapters remain available to backup/privacy while UI capability registration is absent.
        services.AddSingleton<IBondProgressionPolicy, DefaultBondProgressionPolicy>()
            .AddSingleton<SqliteFocusRepository>().AddSingleton<SqliteEventStore>()
            .AddSingleton<SqliteTimelineRepository>().AddSingleton<SqliteBondRepository>()
            .AddSingleton<SqliteFocusCompletionUnit>();
        if (!enabled) return services;
        if (factoryActiveRole is not null)
            services.AddSingleton<IFocusNativeDispatcher>(_ => new WpfFocusNativeDispatcher(Dispatcher.CurrentDispatcher));
        return services.AddSingleton<IFocusSnapshotStore, SqliteFocusSnapshotStore>()
            .AddSingleton<FocusSessionService>()
            .AddSingleton<IFocusSessionService>(provider => provider.GetRequiredService<FocusSessionService>())
            .AddSingleton(provider => new FocusCompactViewModel(
                provider.GetRequiredService<IFocusSessionService>(), provider.GetRequiredService<AppRuntime>(),
                provider.GetRequiredService<IProcessLifetime>().StoppingToken))
            .AddSingleton<ICompactSurface>(provider => provider.GetRequiredService<FocusCompactViewModel>())
            .AddSingleton(provider =>
            {
                var focus = provider.GetRequiredService<IFocusSessionService>();
                var time = provider.GetRequiredService<TimeProvider>();
                System.Collections.Generic.IReadOnlyList<IToolProvider>? nativeTools = null;
                if (factoryActiveRole is not null)
                {
                    var activeRole = factoryActiveRole(provider);
                    nativeTools = NativeFocusTools.Create(focus,
                        provider.GetRequiredService<IFocusNativeDispatcher>(), activeRole);
                }
                return new FocusDesktopPlugin(focus, time, nativeTools);
            })
            .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<FocusDesktopPlugin>());
    }
}
