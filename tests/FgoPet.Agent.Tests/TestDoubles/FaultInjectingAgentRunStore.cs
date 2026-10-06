using System.Collections.Immutable;
using FgoPet.Kernel.Agent;

namespace FgoPet.Agent.Tests.TestDoubles;

internal sealed class FaultInjectingAgentRunStore(IAgentRunStore inner) : IAgentRunStore
{
    private int _commits;
    public int FailOnCommit { get; init; }
    public ValueTask<AgentRunCheckpoint?> LoadAsync(string runId, CancellationToken token) => inner.LoadAsync(runId, token);
    public ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token) =>
        inner.TryCreateAsync(initial, events, token);
    public ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expectedRevision,
        ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        if (Interlocked.Increment(ref _commits) == FailOnCommit) throw new IOException("fixture storage fault");
        return inner.TryCommitAsync(next, expectedRevision, events, token);
    }
}
