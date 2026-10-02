using System.Collections.Immutable;

namespace FgoPet.Extensibility;

public sealed record PluginFailure(string PluginId, string Code);
public sealed record PluginActivationResult(bool Succeeded, string? ErrorCode, ImmutableArray<PluginFailure> Failures);

/// <summary>Owns activation and reverse-order cleanup, independently of any desktop or feature implementation.</summary>
public sealed class PluginRuntime(PluginCatalog catalog) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private ImmutableHashSet<string> _active = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private ImmutableArray<PluginFailure> _failures = [];
    private bool _running;
    private bool _ready;
    private CancellationToken _stopping;
    private bool _closed;
    private int _admissionClosed;

    public bool IsActive(string pluginId) => Volatile.Read(ref _admissionClosed) == 0 && Volatile.Read(ref _active).Contains(pluginId);

    public void CloseAdmission() => Interlocked.Exchange(ref _admissionClosed, 1);

    /// <summary>Emergency synchronous disposal closes visibility only; normal host shutdown awaits StopAsync.</summary>
    public void Dispose()
    {
        CloseAdmission();
        Volatile.Write(ref _active, ImmutableHashSet.Create<string>(StringComparer.Ordinal));
    }

    public async Task<PluginActivationResult> StartAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return new(false, "PLUGIN_START_CANCELLED", []);
        try { await _gate.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return new(false, "PLUGIN_START_CANCELLED", []); }
        try
        {
            if (_closed || Volatile.Read(ref _admissionClosed) != 0) return new(false, "PLUGIN_RUNTIME_STOPPED", _failures);
            if (_running) return new(true, null, []);
            _stopping = stoppingToken;
            string? startingPlugin = null;
            try
            {
                foreach (var plugin in catalog.Plugins)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    startingPlugin = plugin.Manifest.Id;
                    _attempted.Add(plugin.Manifest.Id);
                    await plugin.Instance.StartAsync(stoppingToken);
                    stoppingToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _admissionClosed) != 0) throw new OperationCanceledException();
                    Volatile.Write(ref _active, _active.Add(plugin.Manifest.Id));
                }
                _running = true;
                return new(true, null, []);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || Volatile.Read(ref _admissionClosed) != 0)
            {
                await StopCoreAsync(CancellationToken.None);
                return new(false, "PLUGIN_START_CANCELLED", _failures);
            }
            catch (Exception)
            {
                await StopCoreAsync(CancellationToken.None);
                if (startingPlugin is not null) _failures = _failures.Insert(0, new(startingPlugin, "PLUGIN_START_FAILED"));
                return new(false, "PLUGIN_START_FAILED", _failures);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableArray<PluginFailure>> NotifyApplicationReadyAsync(ApplicationReadiness readiness,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping, cancellationToken);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (_ready || !_running || _closed || Volatile.Read(ref _admissionClosed) != 0) return [];
            _ready = true;
            var failures = ImmutableArray.CreateBuilder<PluginFailure>();
            foreach (var plugin in catalog.Plugins)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (IsActive(plugin.Manifest.Id) && plugin.Instance is IApplicationReadyObserver observer)
                {
                    try { await observer.OnApplicationReadyAsync(readiness, linked.Token); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                    catch (Exception) { failures.Add(new(plugin.Manifest.Id, "PLUGIN_READY_FAILED")); }
                }
            }
            return failures.ToImmutable();
        }
        finally { _gate.Release(); }
    }

    public async Task<ImmutableArray<PluginFailure>> StopAsync(CancellationToken cancellationToken = default)
    {
        CloseAdmission();
        await _gate.WaitAsync();
        try
        {
            if (!_closed) await StopCoreAsync(cancellationToken);
            return _failures;
        }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        CloseAdmission();
        var failures = ImmutableArray.CreateBuilder<PluginFailure>();
        foreach (var plugin in catalog.Plugins.Reverse())
        {
            if (_attempted.Contains(plugin.Manifest.Id))
            {
                try { await plugin.Instance.StopAsync(cancellationToken); }
                catch (Exception) { failures.Add(new(plugin.Manifest.Id, "PLUGIN_STOP_FAILED")); }
            }
            try { await plugin.Instance.DisposeAsync(); }
            catch (Exception) { failures.Add(new(plugin.Manifest.Id, "PLUGIN_DISPOSE_FAILED")); }
        }
        _attempted.Clear();
        Volatile.Write(ref _active, ImmutableHashSet.Create<string>(StringComparer.Ordinal));
        _running = false; _closed = true; _failures = failures.ToImmutable();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
