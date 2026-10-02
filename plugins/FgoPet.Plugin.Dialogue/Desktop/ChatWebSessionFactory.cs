using FgoPet.Dialogue.Contracts;

namespace FgoPet.App.Dialogue;

internal sealed class ChatWebSessionFactory(ConversationViewModel conversation) : IChatWebSessionFactory
{
    public IChatWebSession Create(IChatWebHostActions host) => new ChatWebSession(conversation, host);
}
