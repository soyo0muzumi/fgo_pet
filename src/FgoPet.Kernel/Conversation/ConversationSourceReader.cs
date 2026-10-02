using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>Only the conversation owner can validate original-message provenance.</summary>
public sealed class ConversationSourceReader(IConversationReader conversations) : IConversationSourceReader
{
    public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind)
    {
        var conversation = conversations.GetConversation(scope.ConversationId, scope.RoleId);
        var message = conversations.LoadMessages(scope.ConversationId, scope.RoleId).SingleOrDefault(m => m.MessageId == messageId);
        if (conversation is null || conversation.ProjectId != scope.ProjectId || message is null ||
            message.ServantId != scope.RoleId || message.Status != ChatMessageStatus.Completed ||
            !Enum.IsDefined(kind) || (kind == ConversationSourceKind.User ? message.Role != ChatMessageRole.User : message.Role != ChatMessageRole.Assistant))
            throw new InvalidOperationException("Conversation source is no longer current.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { message.MessageId, message.Role, message.Text, message.CreatedAtUtc }))));
        return new(scope, messageId, fingerprint, message.CreatedAtUtc, kind, conversation.ProjectLabel);
    }
    public bool IsCurrent(ConversationSourceSnapshot source)
    {
        try { return source == Read(source.Scope, source.MessageId, source.Kind); }
        catch (InvalidOperationException) { return false; }
    }
}
