using System.Collections.Immutable;
using System.Diagnostics;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public sealed class AgentEventDispatcher
{
    private readonly PluginCatalog _catalog;
    private readonly PluginRuntime _runtime;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<IAgentRunObserver, Task> _pending = new(ReferenceEqualityComparer.Instance);

    public AgentEventDispatcher(PluginCatalog catalog, PluginRuntime runtime, TimeSpan? dispatchTimeout = null)
    {
        _catalog = catalog;
        _runtime = runtime;
        _timeout = dispatchTimeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(dispatchTimeout));
    }

    public async ValueTask DispatchAsync(ImmutableArray<AgentEvent> events, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (events.IsDefault) throw new ArgumentException("Invalid event batch.", nameof(events));
        var start = Stopwatch.GetTimestamp();
        TimeSpan Remaining() => _timeout - Stopwatch.GetElapsedTime(start);
        if (!await _gate.WaitAsync(_timeout, token)) return;
        try
        {
            foreach (var observer in _catalog.AgentObservers)
            {
                if (_pending.TryGetValue(observer.Provider, out var pending) && !pending.IsCompleted) continue;
                _pending.Remove(observer.Provider);
                foreach (var entry in events)
                {
                    token.ThrowIfCancellationRequested();
                    var remaining = Remaining();
                    if (remaining <= TimeSpan.Zero) return;
                    if (!Current(observer)) break;
                    if (!Safe(entry)) continue;
                    var notification = new AgentRunNotification(entry.Sequence, entry.Timestamp, entry.Kind.ToString(),
                        entry.RunCorrelation, entry.StepNumber, entry.CallCorrelation, entry.ErrorCode);
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cancellation.CancelAfter(remaining);
                    var dispatch = Task.Run(async () =>
                    {
                        if (Current(observer)) await observer.Provider.ObserveAsync(notification, cancellation.Token);
                    }, CancellationToken.None);
                    _pending[observer.Provider] = dispatch;
                    _ = dispatch.ContinueWith(completed =>
                    {
                        _ = completed.Exception;
                        cancellation.Dispose();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    try { await dispatch.WaitAsync(remaining, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (TimeoutException) { return; }
                    catch (Exception) { /* Notification failures cannot drive or fail the run. */ }
                }
            }
        }
        finally { _gate.Release(); }
    }

    private bool Current(RegisteredAgentObserver observer)
    {
        try { return _runtime.IsActive(observer.PluginId) && observer.Provider.Id == observer.ProviderId; }
        catch (Exception) { return false; }
    }
    private static bool Safe(AgentEvent entry) => entry.Sequence > 0 && entry.StepNumber >= 0
        && entry.Timestamp.Offset == TimeSpan.Zero && Enum.IsDefined(entry.Kind) && Hash(entry.RunCorrelation)
        && (entry.CallCorrelation is null || Hash(entry.CallCorrelation))
        && (entry.ErrorCode is null || entry.ErrorCode.Length is > 0 and <= 64
            && AgentRunState.SafeCode(entry.ErrorCode) == entry.ErrorCode);
    private static bool Hash(string? value) => value is { Length: 24 }
        && value.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9');
}
