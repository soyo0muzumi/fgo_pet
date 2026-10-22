using System.Windows;
using System.Windows.Threading;
using FgoPet.Dialogue.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FgoPet.App.Dialogue;

public static class ExternalConversationRegistration
{
    public static IServiceCollection AddExternalConversationGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExternalConversationGateway>(provider => new ExternalConversationGateway(
            provider.GetRequiredService<ConversationViewModel>(), Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
        return services;
    }
}
