using FgoPet.Dialogue.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FgoPet.App.Dialogue;

public static class ChatWebRegistration
{
    /// <summary>Registers the Dialogue-owned adapter over the shared conversation owner.</summary>
    public static IServiceCollection AddChatWebPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IChatWebSessionFactory, ChatWebSessionFactory>();
        return services;
    }
}
