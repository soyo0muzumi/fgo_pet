using System.Windows.Threading;
using FgoPet.App.Focus;

namespace FgoPet.Plugin.Focus.Desktop;

/// <summary>Marshals native Focus calls onto the dispatcher's Focus owner thread.</summary>
public sealed class WpfFocusNativeDispatcher(Dispatcher dispatcher) : IFocusNativeDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public T Invoke<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnsureAvailable();
        return _dispatcher.CheckAccess()
            ? operation()
            : _dispatcher.Invoke(operation, DispatcherPriority.Send);
    }

    public ValueTask<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        if (_dispatcher.CheckAccess()) return ValueTask.FromResult(operation());
        return new ValueTask<T>(_dispatcher.InvokeAsync(operation, DispatcherPriority.Send, cancellationToken).Task);
    }

    private void EnsureAvailable()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("FOCUS_DISPATCHER_CLOSED");
    }
}
