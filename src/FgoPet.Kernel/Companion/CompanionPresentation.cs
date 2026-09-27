using FgoPet.App.Runtime;
using FgoPet.Core.Portraits;

namespace FgoPet.Kernel.Companion;

public sealed record CompanionPresentationState(ActiveRoleState Role, ExpressionSemantic Expression, string? Text);

/// <summary>Owns semantic presentation. Consumers recheck identity before queued UI work applies.</summary>
public sealed class CompanionPresentation : IDisposable
{
    private readonly AppRuntime _identity;
    private readonly object _gate = new();
    private bool _closed;
    public CompanionPresentation(AppRuntime identity)
    {
        _identity = identity;
        identity.ActiveRoleChanged += OnRoleChanged;
    }
    public CompanionPresentationState? Current { get; private set; }
    public ActiveRoleState? ActiveRole => _identity.ActiveRole;
    public event Action<CompanionPresentationState?>? Changed;
    public string? LastFailureCode { get; private set; }

    public bool IsCurrent(CompanionPresentationState state)
    {
        lock (_gate) return !_closed && ReferenceEquals(Current, state) && ReferenceEquals(_identity.ActiveRole, state.Role);
    }
    public bool Apply(ActiveRoleState role, ExpressionSemantic expression, string? text = null)
    {
        if (!Enum.IsDefined(expression) || text?.Length > 4_000) return false;
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(_identity.ActiveRole, role)) return false;
            Current = new(role, expression, text);
            Publish(Current);
            return true;
        }
    }
    private void OnRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args)
    {
        lock (_gate)
        {
            if (_closed) return;
            Current = null;
            Publish(null);
        }
    }
    private void Publish(CompanionPresentationState? state)
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((Action<CompanionPresentationState?>)subscriber)(state); }
            catch (Exception) { LastFailureCode = "PRESENTATION_SUBSCRIBER_FAILED"; }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _identity.ActiveRoleChanged -= OnRoleChanged;
            Current = null;
            Changed = null;
        }
    }
}
