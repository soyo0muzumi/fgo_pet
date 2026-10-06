using System.Collections.Immutable;
using FgoPet.Kernel.Agent;

namespace FgoPet.Agent.Tests.TestDoubles;

public sealed class ScriptedFaultingRunStore(IAgentRunStore inner, AgentEventKind failAt) : IAgentRunStore
{
    public ValueTask<AgentRunCheckpoint?> LoadAsync(string runId, CancellationToken token)
        => inner.LoadAsync(runId, token);
    public ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token)
        => inner.TryCreateAsync(initial, events, token);
    public ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expectedRevision,
        ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        if (events.Any(item => item.Kind == failAt)) throw new IOException("SYNTHETIC_STORE_FAULT");
        return inner.TryCommitAsync(next, expectedRevision, events, token);
    }
}
