using FgoPet.Dialogue.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FgoPet.App.Dialogue;

public static class ChatWebRegistration
{
    /// <summary>Explicit local presentation root; artwork is not packaged with the application.</summary>
    public static Func<string, CancellationToken, Task<string?>> CreateLocalStickerLoader(string root, string? libraryRoot = null)
    {
        var original = new LocalStickerLoader(root);
        var collected = libraryRoot is null ? null : new ManagedStickerLibrary(libraryRoot);
        return (id, token) => StickerPolicy.IsWebAssetId(id) && collected is not null
            ? collected.LoadAsync(id, token) : original.LoadAsync(id, token);
    }

    public static WebStickerPlugin CreateWebStickerPlugin(IStickerCatalogSource library, FgoPet.Core.Dialogue.IConversationReader conversations,
        FgoPet.Core.Dialogue.IConversationScopeAuthority scopeAuthority,
        Func<FgoPet.Extensibility.ToolScope, Func<bool>?> captureWeb, Func<FgoPet.Extensibility.ToolAvailability> availability,
        Action<StickerImportDiagnostic>? diagnostics = null) => new(library as ManagedStickerLibrary
            ?? throw new ArgumentException("Managed sticker catalog required.", nameof(library)),
            string.Equals(Environment.GetEnvironmentVariable("FGO_PET_STICKER_IMPORT"), "1", StringComparison.Ordinal),
            scope => scopeAuthority.ActiveConversation == scope && conversations.GetConversation(scope.ConversationId, scope.RoleId) is { IsArchived: false } conversation
                && StickerPolicy.IsMash(scope.RoleId, conversation.ContentContext.PackageId),
            captureWeb, availability, diagnostics);

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
