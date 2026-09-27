using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace FgoPet.Kernel.Lifecycle;

public sealed record LifecycleError(string Code, string Operation);

/// <summary>Registers work before invoking it and observes every admitted completion during shutdown.</summary>
public sealed class BackgroundOperationTracker(ProcessLifetime lifetime)
{
    private readonly ConcurrentDictionary<long, Task> _operations = new();
    private readonly ConcurrentQueue<LifecycleError> _failures = new();
    private long _sequence;
    public ImmutableArray<LifecycleError> Failures => _failures.ToImmutableArray();

    public Task RunAsync(string code, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(code, async token => { await operation(token); return true; }, cancellationToken);
    }

    public Task<T> RunAsync<T>(string code, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ValidateCode(code);
        ArgumentNullException.ThrowIfNull(operation);
        var id = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!lifetime.TryAdmit(() => _operations[id] = completion.Task))
        {
            completion.TrySetCanceled(lifetime.StoppingToken);
            return completion.Task;
        }
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            CancellationTokenSource? linked = null;
            try
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.StoppingToken, cancellationToken);
                linked.Token.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (linked?.IsCancellationRequested == true) { completion.TrySetCanceled(linked.Token); }
            catch (Exception error)
            {
                _failures.Enqueue(new("BACKGROUND_OPERATION_FAILED", code));
                while (_failures.Count > 64) _failures.TryDequeue(out _);
                completion.TrySetException(error);
            }
            finally { linked?.Dispose(); _operations.TryRemove(id, out _); }
        }
    }

    public async Task DrainAsync()
    {
        if (!lifetime.IsStopping) throw new InvalidOperationException("Close process admission before draining.");
        try { await Task.WhenAll(_operations.Values.ToArray()).ConfigureAwait(false); }
        catch (Exception) { /* Individual faults were observed and recorded with safe codes. */ }
    }

    internal static void ValidateCode(string code)
    {
        if (code is null || !Regex.IsMatch(code, "\\A[A-Z][A-Z0-9_]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Lifecycle diagnostic identifiers must be fixed operation codes.");
    }
}
