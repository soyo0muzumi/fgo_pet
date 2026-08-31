using FgoPet.AgentRuntime;
using FgoPet.Core.Agents;

namespace FgoPet.Infrastructure.Agents;

/// <summary>One cancellable polling owner, independent of portrait visibility.</summary>
public sealed class AgentRelayRuntime : IAgentRelayRuntime, IDisposable
{
    private readonly AgentRelayClient _gateway;
    private readonly IAgentRelayAdministration _administration;
    private readonly Func<CancellationToken, Task<RelayBootstrapResult>> _bootstrap;
    private readonly Func<IReadOnlyList<AgentEvent>, CancellationToken, Task> _apply;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private volatile AgentRelaySnapshot _current = AgentRelaySnapshot.Disabled;
    private IReadOnlyList<AgentEvent> _pendingProjection = [];
    private bool _disposed;

    public AgentRelayRuntime(AgentRelayClient gateway, IAgentRelayAdministration administration,
        Func<CancellationToken, Task<RelayBootstrapResult>> bootstrap,
        Func<IReadOnlyList<AgentEvent>, CancellationToken, Task> apply, TimeSpan? interval = null)
    {
        _gateway = gateway;
        _administration = administration;
        _bootstrap = bootstrap;
        _apply = apply;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public AgentRelaySnapshot Current => _current;
    public event Action<AgentRelaySnapshot>? SnapshotChanged;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (enabled)
                {
                    if (!_worker.IsCompleted) return;
                    _cancellation?.Dispose();
                    _cancellation = new CancellationTokenSource();
                    var token = _cancellation.Token;
                    _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
                    return;
                }
                _cancellation?.Cancel();
            }
            await _worker.ConfigureAwait(false);
            // Disabling never launches Relay. If it is running, stop acceptance now.
            try { await _gateway.SetConnectionEnabledAsync(false, cancellationToken).ConfigureAwait(false); }
            catch (AgentRelayException error) when (error.SafeError is "relay_offline" or "relay_timeout") { }
            Publish(AgentRelaySnapshot.Disabled);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync) _cancellation?.Cancel();
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            Publish(AgentRelaySnapshot.Disabled);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var ready = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!ready)
                    {
                        var boot = await _bootstrap(cancellationToken).ConfigureAwait(false);
                        if (boot.Status != RelayBootstrapStatus.Ready)
                            throw new AgentRelayException(boot.Status == RelayBootstrapStatus.VersionMismatch ? "version_mismatch" : "relay_offline");
                        await _gateway.SetConnectionEnabledAsync(true, cancellationToken).ConfigureAwait(false);
                        ready = true;
                    }
                    // Retain a received batch until projection succeeds; don't drain more on local failure.
                    if (_pendingProjection.Count == 0)
                        _pendingProjection = await _gateway.PollPendingEventsAsync(cancellationToken).ConfigureAwait(false);
                    if (_pendingProjection.Count > 0)
                    {
                        await _apply(_pendingProjection, cancellationToken).ConfigureAwait(false);
                        _pendingProjection = [];
                    }
                    var snapshot = await _administration.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    ready = snapshot.RelayOnline && _gateway.IsConnected;
                    cancellationToken.ThrowIfCancellationRequested();
                    Publish(snapshot);
                }
                catch (AgentRelayException error)
                {
                    ready = false;
                    Publish(AgentRelayAdministration.Failure(error.SafeError));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Keep the desktop alive and retry the same batch; don't expose paths/payloads.
                    Publish(AgentRelayAdministration.Failure("local_projection_unavailable"));
                }
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Publish(AgentRelaySnapshot snapshot)
    {
        if (_disposed) return;
        _current = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation?.Cancel();
            var cancellation = _cancellation;
            _ = _worker.ContinueWith(_ => cancellation?.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
