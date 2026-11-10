namespace FgoPet.Core.Memory;

public sealed record MemoryRecallSnapshot(long Revision, IReadOnlyList<StoredMemory> Items);
public interface IMemoryRecall
{
    MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000);

    /// <summary>
    /// Recall that must return only memories matched by the query, never an unrelated
    /// general-purpose background set. Prompt injection and native context use this so a
    /// turn with no matching confirmed evidence stays empty. The default implementation preserves plain
    /// Query behavior for implementations without a distinct strict path.
    /// </summary>
    MemoryRecallSnapshot QueryStrict(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000)
        => Query(scope, query, maxItems, maxChars);

    /// <summary>Matched memories for historical review, including sourced automatic records.</summary>
    MemoryRecallSnapshot QueryHistory(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000)
        => QueryStrict(scope, query, maxItems, maxChars);

    MemoryRecallSnapshot QueryForExtraction(MemoryScope scope, string query, int maxItems = 8, int maxChars = 4000)
        => Query(scope, query, maxItems, maxChars);
}
public interface IMemorySnapshotReader
{
    MemoryRecallSnapshot ReadSnapshot(MemoryScope scope);
}

internal interface IMemorySearchReader : IMemorySnapshotReader
{
    (MemoryRecallSnapshot Snapshot, IReadOnlyDictionary<string, double> Ranks) ReadSearchSnapshot(
        MemoryScope scope, IReadOnlyList<string> indexTerms);
}
