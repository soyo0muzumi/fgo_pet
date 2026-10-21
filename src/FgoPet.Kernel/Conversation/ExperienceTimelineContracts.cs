using FgoPet.Extensibility;

namespace FgoPet.Core.Dialogue;

public sealed record ExperienceWindowCursor(int StartSequence, int ThroughSequence, int Offset, string Version);
public sealed record ExperienceWindowItem(HistoryAnchor Anchor, ConversationSourceSnapshot Source,
    string Excerpt, int IncludedScalars, int TotalScalars, bool Complete, long SourceSequence = 0);
public sealed record ExperienceWindowPage(ExperienceSourceStatus Status, IReadOnlyList<ExperienceWindowItem> Items,
    ExperienceWindowCursor? Next = null, string? Version = null, int ThroughSequence = 0, bool HasLaterMessages = false);
public sealed record ExperienceScanCursor(long Sequence, string MessageId);
public sealed record ExperienceSourceHead(HistoryAnchor Anchor, ConversationSourceSnapshot Source, int TotalScalars);
public sealed record ExperienceScanPage(ExperienceSourceStatus Status, IReadOnlyList<ExperienceSourceHead> Items,
    ExperienceScanCursor Cursor, bool HasMore);

/// <summary>Dialogue-owned fixed-version, bounded completed-original window. Excerpts never imply full coverage.</summary>
public interface IExperienceTimeline : IConversationSourceReader
{
    ExperienceSourceStatus ValidateSource(ConversationSourceSnapshot source) => IsCurrent(source) ? ExperienceSourceStatus.Available : ExperienceSourceStatus.Changed;
    long Boundary(ConversationScope scope);
    long InputRevision(ConversationScope scope) => throw new InvalidOperationException("EXPERIENCE_SCAN_UNAVAILABLE");
    ExperienceScanPage ScanHeads(ConversationScope scope, ExperienceScanCursor cursor, int maxItems = 32,
        CancellationToken token = default) => throw new InvalidOperationException("EXPERIENCE_SCAN_UNAVAILABLE");
    ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1,
        ExperienceWindowCursor? cursor = null, int maxItems = 8, int maxScalars = 6000, CancellationToken token = default);
    ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default);
}
