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
        services.TryAddSingleton<IChatWorkspaceSelection, ChatWorkspaceSelection>();
        services.TryAddSingleton<IChatImageSelection, ChatImageSelection>();
        services.TryAddSingleton<IChatWindowSelection>(provider =>
        {
            var conversation = provider.GetRequiredService<ConversationViewModel>();
            var selection = new ChatWindowSelection(provider.GetRequiredService<FgoPet.Platform.WindowText.IWindowTextReader>(),
                conversation.WindowContextScopeKey, () => conversation.CanChooseWindowContext);
            conversation.BindWindowSelection(selection);
            return selection;
        });
        return services;
    }
}
