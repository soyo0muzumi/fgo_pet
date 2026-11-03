using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.App.Dialogue;

/// <summary>Request-local authority. Neither persisted history nor a model-supplied scope grants access.</summary>
public sealed class IndependentConversationScope
{
    private readonly object gate = new();
    private ToolScope? current;
    public bool IsActive(ToolScope scope) { lock (gate) return current == scope; }
    internal IDisposable Enter(ToolScope scope)
    {
        lock (gate)
        {
            if (current is not null) throw new InvalidOperationException("REMOTE_SESSION_BUSY");
            current = scope;
            return new Lease(this, scope);
        }
    }
    private sealed class Lease(IndependentConversationScope owner, ToolScope scope) : IDisposable
    {
        public void Dispose() { lock (owner.gate) { if (owner.current == scope) owner.current = null; } }
    }
}

/// <summary>Remote images never replace the native desktop selection.</summary>
public sealed class IndependentConversationImages(IndependentConversationScope scope, TimeProvider clock,
    Func<ModelRouteKey?> route)
{
    public SelectedImageStore Store { get; } = new(scope.IsActive, clock, route);
}
