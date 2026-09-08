using FgoPet.AgentProtocol.Messages;

namespace FgoPet.CodexAdapter.AppServer;

public enum CodexArchiveBatchPhase
{
    Prepared,
    Committed,
}

public sealed record CodexArchiveBatch(
    string BatchId,
    string BatchSha256,
    CodexArchiveBatchPhase Phase,
    IReadOnlyList<AgentArchiveProtocolItem> Items);

/// <summary>
/// Adapter-local archive state. Tombstones contain only opaque identities and
/// terminal metadata, so pruning the dispatch journal cannot remove replay
/// protection or task content boundaries.
/// </summary>
public sealed record CodexArchiveState(
    int SchemaVersion = 1,
    CodexArchiveBatch? ActiveBatch = null,
    IReadOnlyList<AgentArchiveProtocolItem>? Tombstones = null)
{
    public static CodexArchiveState Empty { get; } = new(1, null, Array.Empty<AgentArchiveProtocolItem>());
}
