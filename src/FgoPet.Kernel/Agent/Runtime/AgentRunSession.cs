using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

internal sealed class AgentRunSession(AgentRunState state, ImmutableArray<ModelMessage> initial) : IDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    public AgentRunState State { get; } = state;
    public SemaphoreSlim AdvanceGate { get; } = new(1, 1);
    public ImmutableArray<ModelMessage> Transcript { get; private set; } = initial;
    public IAgentModelStep? Model { get; set; }
    public ProtectedQueryReference? Query { get; init; }
    public ImmutableDictionary<string, SkillContent> SkillBodies { get; set; } = ImmutableDictionary<string, SkillContent>.Empty;
    public CancellationToken Token
    {
        get { lock (_lifetimeGate) { return _disposed ? new CancellationToken(true) : _lifetime.Token; } }
    }
    public void Cancel()
    {
        lock (_lifetimeGate) { if (!_disposed) _lifetime.Cancel(); }
    }
    public void Append(ModelMessage message)
    {
        var next = Transcript.Add(message);
        StepEnvironmentBuilder.CheckSize(next);
        Transcript = next;
    }
    public void SetPreparedTranscript(ImmutableArray<ModelMessage> prepared)
    {
        ModelProtocol.ValidateTranscript(prepared);
        StepEnvironmentBuilder.CheckSize(prepared);
        var accepted = Transcript.SelectMany(message => message.ToolCalls).ToHashSet();
        if (prepared.SelectMany(message => message.ToolCalls).Any(call => !accepted.Contains(call)))
            throw new AgentProtocolException("RUN_PREPARATION_INVALID_CALLS");
        Transcript = prepared;
    }
    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            Transcript = [];
            Model = null;
            SkillBodies = ImmutableDictionary<string, SkillContent>.Empty;
            _lifetime.Dispose();
        }
        // A cancelling caller can still be waiting on AdvanceGate; it owns no persistent state.
    }
}
