using System.Windows.Threading;
using FgoPet.App.Tray;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Plugin.Reminders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

internal static class ReminderRegistration
{
    internal static IServiceCollection AddReminderCapability(this IServiceCollection services) => services
        .AddSingleton(p => new ReminderService(p.GetRequiredService<RuntimeDatabase>(), p.GetRequiredService<TimeProvider>()))
        .AddSingleton<IDerivedConversationData>(p => p.GetRequiredService<ReminderService>())
        .AddSingleton<IReminderNotifier>(p => new WindowsReminderNotifier(() => p.GetRequiredService<TrayService>()))
        .AddSingleton(p => new ReminderScheduler(p.GetRequiredService<ReminderService>(), p.GetRequiredService<IReminderNotifier>(),
            p.GetRequiredService<IDialogueContextLifetime>(), p.GetRequiredService<TimeProvider>(),
            d => p.GetRequiredService<ILogger<ReminderScheduler>>().LogInformation(
                "Reminder {Timestamp} {Correlation} {Stage} {ErrorCode}", d.Timestamp, d.Correlation, d.Stage, d.ErrorCode)))
        .AddSingleton<IFgoPetPlugin>(p => new RemindersPlugin(p.GetRequiredService<ReminderService>(), p.GetRequiredService<ReminderScheduler>(),
            p.GetRequiredService<IConversationScopeAuthority>().IsActive, p.GetRequiredService<TimeProvider>()));

    private sealed class WindowsReminderNotifier(Func<TrayService> tray) : IReminderNotifier
    {
        public async ValueTask<ReminderNotificationOutcome> NotifyAsync(Reminder reminder, CancellationToken token)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return ReminderNotificationOutcome.Unavailable;
            var attempted = false;
            try
            {
                return await dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return ReminderNotificationOutcome.NotAttempted;
                    attempted = true;
                    return tray().ShowNotification("FGO Pet · 提醒", reminder.Message)
                        ? ReminderNotificationOutcome.Submitted : ReminderNotificationOutcome.Unavailable;
                }, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!attempted) { return ReminderNotificationOutcome.NotAttempted; }
        }
    }
}
