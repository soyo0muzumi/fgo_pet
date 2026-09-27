using FgoPet.Core.Memory;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>Maps validated, neutral conversation provenance into Memory's own receipt format.</summary>
public sealed class MemoryExtractionSourceReader(IConversationSourceReader conversations)
{
    public MemorySource Read(ToolScope scope, string messageId, MemoryEvidenceKind kind)
    {
        var source = conversations.Read(scope, messageId, ToSourceKind(kind));
        return new(new(scope.RoleId, scope.ProjectId), scope.ConversationId, messageId, source.Fingerprint,
            source.OccurredAtUtc, kind, source.ProjectLabel);
    }
    public bool IsCurrent(MemorySource source) => conversations.IsCurrent(new(
        new(source.ConversationId, source.Scope.ServantId, source.Scope.ProjectId), source.MessageId,
        source.SourceFingerprint, source.OccurredAtUtc, ToSourceKind(source.Kind), source.ProjectLabel));
    private static ConversationSourceKind ToSourceKind(MemoryEvidenceKind kind) => kind switch
    {
        MemoryEvidenceKind.UserStatement => ConversationSourceKind.User,
        MemoryEvidenceKind.AssistantSuggestion => ConversationSourceKind.Assistant,
        _ => throw new InvalidOperationException("Unsupported conversation evidence kind.")
    };
}
