using System.Windows.Threading;
using FgoPet.App.Memory;
using FgoPet.App.Runtime;

namespace FgoPet.Plugin.Memory.Desktop;

/// <summary>
/// Memory-owned projection of the Kernel active role into memory and data-management UI.
/// Uses the Kernel identity and reads the current role after dispatcher delivery.
/// </summary>
internal sealed class MemoryContextConnector : IDisposable
{
    private readonly AppRuntime _runtime;
    private readonly MemoryViewModel _model;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public MemoryContextConnector(AppRuntime runtime, MemoryViewModel model, Dispatcher dispatcher)
    {
        _runtime = runtime;
        _model = model;
        _dispatcher = dispatcher;
        _runtime.ActiveRoleChanged += OnActiveRoleChanged;
        Synchronize();
    }

    private void OnActiveRoleChanged(object? sender, AppStateChangedEventArgs<ActiveRoleState> args) => Synchronize();

    private void Synchronize()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(Synchronize));
            return;
        }

        // Read the current identity when dispatched, not a potentially stale
        // worker-thread notification. The runtime remains the source of truth.
        _model.SetActiveServant(_runtime.ActiveRole?.ServantId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.ActiveRoleChanged -= OnActiveRoleChanged;
    }
}
