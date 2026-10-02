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

namespace FgoPet.Plugin.Focus.Desktop;

public static class FocusRegistration
{
    public static IServiceCollection AddFocusCapability(this IServiceCollection services, bool enabled = true)
    {
        // Data adapters remain available to backup/privacy while UI capability registration is absent.
        services.AddSingleton<IBondProgressionPolicy, DefaultBondProgressionPolicy>()
            .AddSingleton<SqliteFocusRepository>().AddSingleton<SqliteEventStore>()
            .AddSingleton<SqliteTimelineRepository>().AddSingleton<SqliteBondRepository>()
            .AddSingleton<SqliteFocusCompletionUnit>();
        if (!enabled) return services;
        return services.AddSingleton<IFocusSnapshotStore, SqliteFocusSnapshotStore>()
            .AddSingleton<FocusSessionService>()
            .AddSingleton<IFocusSessionService>(provider => provider.GetRequiredService<FocusSessionService>())
            .AddSingleton(provider => new FocusCompactViewModel(
                provider.GetRequiredService<IFocusSessionService>(), provider.GetRequiredService<AppRuntime>(),
                provider.GetRequiredService<IProcessLifetime>().StoppingToken))
            .AddSingleton<ICompactSurface>(provider => provider.GetRequiredService<FocusCompactViewModel>())
            .AddSingleton<FocusDesktopPlugin>()
            .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<FocusDesktopPlugin>());
    }
}
