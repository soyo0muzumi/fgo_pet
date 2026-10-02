namespace FgoPet.Core.Memory;

public sealed record MemoryRecallSnapshot(long Revision, IReadOnlyList<StoredMemory> Items);
public interface IMemoryRecall
{
    MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000);
}
public interface IMemorySnapshotReader
{
    MemoryRecallSnapshot ReadSnapshot(MemoryScope scope);
}
