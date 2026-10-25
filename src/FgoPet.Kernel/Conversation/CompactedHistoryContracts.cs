using FgoPet.Extensibility;

namespace FgoPet.Core.Dialogue;

/// <summary>Original messages in the validated compacted prefix of the invocation's current conversation.</summary>
public interface ICompactedHistoryRepository
{
    CompactedHistoryPage Search(ToolScope scope, string query, int afterSequence = 0,
        int maxItems = 8, int maxChars = 6000, CancellationToken cancellationToken = default);
    CompactedHistoryPage Read(ToolScope scope, string messageId, int offset = 0,
        int maxChars = 6000, CancellationToken cancellationToken = default);
}

/// <summary>Offsets count Unicode scalar values, not UTF-16 code units. NextSequence is an exclusive search cursor.</summary>
public sealed record CompactedHistoryPage(long ProjectionRevision, int CoveredThroughSequence,
    IReadOnlyList<CompactedHistoryItem> Items, int? NextSequence = null);
public sealed record CompactedHistoryItem(string MessageId, int Sequence, string Role, string Status,
    DateTimeOffset CreatedAtUtc, string Text, int Offset, int? NextOffset);
