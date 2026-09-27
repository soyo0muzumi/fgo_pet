using FgoPet.Core.Agents;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Agents;
using FgoPet.Kernel.Lifecycle;

namespace FgoPet.AgentBackend;

/// <summary>Owns backend bootstrap/recovery and shutdown; contributes no task UI or Todo capability.</summary>
public sealed class AgentBackendPlugin(IAgentRelayRuntime runtime, AgentReconnectService reconnect,
    Func<bool> enabled, BackgroundOperationTracker operations) : IFgoPetPlugin, IApplicationReadyObserver, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _owned = [];
    private CancellationTokenSource? _cancellation;
    private bool _started;
    private bool _closed;
    private bool _storageAvailable;
    private int _ready;
    private int _reconnectReady;
    private Task? _stop;
    public PluginManifest Manifest { get; } = new("backend.agent", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty;

    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_started) return ValueTask.CompletedTask;
            _started = true;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (runtime is AgentRelayRuntime owner) owner.BindLifecycle(_cancellation.Token);
            runtime.SnapshotChanged += OnSnapshot;
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _ready, 1) != 0) return ValueTask.CompletedTask;
        _storageAvailable = readiness.StorageAvailable;
        var shouldEnable = _storageAvailable && enabled();
        StartObserved("AGENT_BACKEND_START", token => runtime.SetEnabledAsync(shouldEnable, token));
        if (shouldEnable) StartObserved("AGENT_BACKEND_RECONNECT", token => reconnect.ReconnectAsync(token));
        return ValueTask.CompletedTask;
    }
    private void OnSnapshot(AgentRelaySnapshot snapshot)
    {
        if (!_storageAvailable || !enabled() || !snapshot.RelayOnline)
        {
            Volatile.Write(ref _reconnectReady, 0);
            return;
        }
        if (Interlocked.Exchange(ref _reconnectReady, 1) == 0)
            StartObserved("AGENT_BACKEND_RECONNECT", token => reconnect.ReconnectAsync(token));
    }
    private void StartObserved(string code, Func<CancellationToken, Task> action)
    {
        TaskCompletionSource completion;
        CancellationToken token;
        lock (_gate)
        {
            if (_closed || !_started || _cancellation!.IsCancellationRequested) return;
            token = _cancellation.Token;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _owned.Add(completion.Task);
        }
        _ = RunAsync();
        async Task RunAsync()
        {
            try { await operations.RunAsync(code, action, token).ConfigureAwait(false); }
            catch (Exception) { } // The process tracker retains only fixed failure codes.
            finally
            {
                lock (_gate) _owned.Remove(completion.Task);
                completion.TrySetResult();
            }
        }
    }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return new(_stop ??= StopCoreAsync());
    }
    private async Task StopCoreAsync()
    {
        Task[] owned;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _closed = true;
            runtime.SnapshotChanged -= OnSnapshot;
            cancellation = _cancellation;
            owned = _owned.ToArray();
        }
        cancellation?.Cancel();
        try { await runtime.StopAsync().ConfigureAwait(false); }
        finally
        {
            await Task.WhenAll(owned).ConfigureAwait(false);
            cancellation?.Dispose();
        }
    }
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    public void Dispose()
    {
        lock (_gate) { _closed = true; runtime.SnapshotChanged -= OnSnapshot; }
        try { _cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
