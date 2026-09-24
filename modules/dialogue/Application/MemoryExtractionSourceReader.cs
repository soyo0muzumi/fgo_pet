using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;

namespace FgoPet.App.Dialogue;

/// <summary>Dialogue validates its raw source; Memory receives only a trusted provenance snapshot.</summary>
public sealed class MemoryExtractionSourceReader(IConversationReader conversations)
{
    public MemorySource Read(ConversationScope scope, string conversationId, string messageId, MemoryEvidenceKind kind)
    {
        var conversation = conversations.GetConversation(conversationId, scope.ServantId);
        var message = conversations.LoadMessages(conversationId, scope.ServantId).SingleOrDefault(m => m.MessageId == messageId);
        if (conversation is null || conversation.ProjectId != scope.ProjectId || message is null ||
            message.ServantId != scope.ServantId || message.Status != ChatMessageStatus.Completed ||
            kind is not (MemoryEvidenceKind.UserStatement or MemoryEvidenceKind.AssistantSuggestion) ||
            (kind == MemoryEvidenceKind.UserStatement ? message.Role != ChatMessageRole.User : message.Role != ChatMessageRole.Assistant))
            throw new InvalidOperationException("Memory source is no longer current.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { message.MessageId, message.Role, message.Text, message.CreatedAtUtc }))));
        return new(new(scope.ServantId, scope.ProjectId), conversationId, messageId, fingerprint, message.CreatedAtUtc, kind, conversation.ProjectLabel);
    }
    public bool IsCurrent(MemorySource source)
    {
        try { return source == Read(new(source.Scope.ServantId, source.Scope.ProjectId), source.ConversationId, source.MessageId, source.Kind); }
        catch (InvalidOperationException) { return false; }
    }
}
