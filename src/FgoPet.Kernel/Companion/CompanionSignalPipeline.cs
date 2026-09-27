using FgoPet.Extensibility;

namespace FgoPet.Kernel.Companion;

/// <summary>Routes only declared, active semantic sources. Owns and releases every subscription.</summary>
public sealed class CompanionSignalPipeline : IDisposable
{
    private readonly PluginRuntime _runtime;
    private readonly List<(ICompanionSignalSource Source, Action<CompanionSignal> Handler)> _subscriptions = [];
    private int _closed;
    public CompanionSignalPipeline(PluginCatalog catalog, PluginRuntime runtime)
    {
        _runtime = runtime;
        foreach (var entry in catalog.Signals)
        {
            Action<CompanionSignal> handler = signal => Publish(entry.PluginId, signal);
            _subscriptions.Add((entry.Provider, handler));
            entry.Provider.Signal += handler;
        }
    }
    public event Action<CompanionSignal>? Signal;
    public string? LastFailureCode { get; private set; }
    private void Publish(string pluginId, CompanionSignal signal)
    {
        if (Volatile.Read(ref _closed) != 0 || !_runtime.IsActive(pluginId)) return;
        foreach (var subscriber in Signal?.GetInvocationList() ?? [])
        {
            try { ((Action<CompanionSignal>)subscriber)(signal); }
            catch (Exception) { LastFailureCode = "COMPANION_SIGNAL_HANDLER_FAILED"; }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        foreach (var (source, handler) in _subscriptions) source.Signal -= handler;
        _subscriptions.Clear();
        Signal = null;
    }
}
