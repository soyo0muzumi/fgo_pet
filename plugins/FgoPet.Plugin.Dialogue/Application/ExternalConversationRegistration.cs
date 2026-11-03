using System.Windows;
using System.Windows.Threading;
using FgoPet.Dialogue.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;

namespace FgoPet.App.Dialogue;

public static class ExternalConversationRegistration
{
    public static IServiceCollection AddExternalConversationGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExternalConversationGateway>(provider => new ExternalConversationGateway(
            provider.GetRequiredService<ConversationViewModel>(), Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
        services.TryAddSingleton<IIndependentConversationGateway>(provider => new IndependentConversationGateway(
            provider.GetRequiredService<ConversationViewModel>(), provider.GetRequiredService<ConversationOrchestrator>(),
            provider.GetRequiredService<IDialogueSettingsStore>(), provider.GetRequiredService<IndependentConversationScope>(),
            provider.GetRequiredService<IndependentConversationImages>(), Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
