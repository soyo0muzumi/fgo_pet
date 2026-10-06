using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Kernel.Agent;

namespace FgoPet.Agent.Tests.TestDoubles;

/// <summary>Test adapter only. Checkpoint and journal are swapped under the same lock.</summary>
internal sealed class InMemoryAgentRunStore : IAgentRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (AgentRunCheckpoint State, ImmutableArray<AgentEvent> Events)> _runs = new(StringComparer.Ordinal);

    public ValueTask<AgentRunCheckpoint?> LoadAsync(string runId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_runs.TryGetValue(runId, out var stored) ? Clone(stored.State) : null);
    }

    public ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_runs.ContainsKey(initial.Snapshot.Identity.RunId) || initial.Snapshot.Revision != 0
                || !ValidEvents(0, initial.JournalSequence, events)) return ValueTask.FromResult(false);
            _runs.Add(initial.Snapshot.Identity.RunId, (Clone(initial), events));
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expectedRevision,
        ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_runs.TryGetValue(next.Snapshot.Identity.RunId, out var stored)
                || stored.State.Snapshot.Revision != expectedRevision || next.Snapshot.Revision != expectedRevision + 1
                || next.Snapshot.Identity != stored.State.Snapshot.Identity
                || !ValidEvents(stored.State.JournalSequence, next.JournalSequence, events)) return ValueTask.FromResult(false);
            _runs[next.Snapshot.Identity.RunId] = (Clone(next), stored.Events.AddRange(events));
            return ValueTask.FromResult(true);
        }
    }

    public ImmutableArray<AgentEvent> Events(string runId) { lock (_gate) return _runs[runId].Events; }

    private static bool ValidEvents(long previous, long next, ImmutableArray<AgentEvent> events) =>
        !events.IsDefaultOrEmpty && next == previous + events.Length
        && events.Select((e, index) => e.Sequence == previous + index + 1).All(value => value);

    private static AgentRunCheckpoint Clone(AgentRunCheckpoint state) => state with
    {
        Calls = state.Calls.Select(call => call.Result is null ? call : call with
        {
            Result = call.Result with { Payload = call.Result.Payload.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement(new { }) : call.Result.Payload.Clone() }
        }).ToImmutableArray()
    };
}
