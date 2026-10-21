using FgoPet.Extensibility;

namespace FgoPet.Core.Dialogue;

public enum ExperienceSourceStatus { Available, Deleted, ScopeDenied, Changed, Unavailable }

/// <summary>A single authoritative completed message, never a conversation summary or derived understanding.</summary>
public sealed record ExperienceMessage(ConversationScope Scope, HistoryAnchor Anchor,
    ConversationSourceKind Kind, string Text, string SourceVersion, string? SourceLabel = null, string? Author = null);

public sealed record ExperienceSourceRead(ExperienceSourceStatus Status, ExperienceMessage? Source = null);

/// <summary>The Dialogue owner reads and versions one original message inside its storage boundary.</summary>
public interface IExperienceSourceReader
{
    ExperienceSourceRead Read(ConversationScope scope, HistoryAnchor anchor, CancellationToken cancellationToken = default);
}
