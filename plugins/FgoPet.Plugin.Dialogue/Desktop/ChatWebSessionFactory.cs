using FgoPet.Dialogue.Contracts;

namespace FgoPet.App.Dialogue;

internal sealed class ChatWebSessionFactory(ConversationViewModel conversation, IStickerCatalogSource? catalog = null) : IChatWebSessionFactory
{
    public IChatWebSession Create(IChatWebHostActions host) => new ChatWebSession(conversation, host, catalog);
}
