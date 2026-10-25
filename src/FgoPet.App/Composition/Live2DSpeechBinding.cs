using System.Windows;
using System.Windows.Threading;
using FgoPet.App.Runtime;

namespace FgoPet.App.Composition;

/// <summary>Host-owned wiring: a playback session retains the role present at its start.</summary>
internal sealed class Live2DSpeechBinding : IDisposable
{
    private readonly AppRuntime _identity;
    private readonly Action<double?> _apply;
    private readonly Func<ActiveRoleState, bool> _accepts;
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private long _generation;
    private int _disposed;

    public Live2DSpeechBinding(AppRuntime identity, Action<double?> apply, Func<ActiveRoleState, bool> accepts)
    {
        _identity = identity; _apply = apply; _accepts = accepts;
        identity.ActiveRoleChanged += OnRoleChanged;
    }

    public Action<double?>? CreateObserver()
    {
        var generation = Interlocked.Increment(ref _generation);
        var role = _identity.ActiveRole;
        if (role is null || Volatile.Read(ref _disposed) != 0) return null;
        return level => Dispatch(() =>
        {
            if (generation == Volatile.Read(ref _generation) && ReferenceEquals(role, _identity.ActiveRole)
                && _accepts(role)) _apply(level);
        });
    }

    private void OnRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args)
    {
        var generation = Interlocked.Increment(ref _generation);
        Dispatch(() => { if (generation == Volatile.Read(ref _generation)) _apply(null); });
    }

    private void Dispatch(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() => { if (Volatile.Read(ref _disposed) == 0) action(); });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _generation);
        _identity.ActiveRoleChanged -= OnRoleChanged;
    }
}
