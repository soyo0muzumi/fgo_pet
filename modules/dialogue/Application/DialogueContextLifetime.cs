using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>Owner-local cancellation and commit fence; invalidation waits for the old leases to drain.</summary>
public sealed class DialogueContextLifetime : IDialogueContextLifetime
{
    private readonly object _gate = new();
    private readonly HashSet<Lease> _active = [];
    private long _generation;
    private int _suspensions;
    public long Generation { get { lock (_gate) return _generation; } }
    public Lease Acquire(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_suspensions > 0) throw new OperationCanceledException("Dialogue maintenance is in progress.", cancellationToken);
            var lease = new Lease(this, _generation, cancellationToken);
            _active.Add(lease);
            return lease;
        }
    }
    private Lease[] CancelOld()
    {
        Lease[] active;
        lock (_gate) { _generation++; active = _active.ToArray(); }
        foreach (var lease in active) lease.Cancel();
        return active;
    }
    public void Invalidate() => CancelOld();
    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        var active = CancelOld();
        await Task.WhenAll(active.Select(lease => lease.Completion)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<IDisposable> SuspendAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _suspensions++;
        try
        {
            await InvalidateAsync(cancellationToken).ConfigureAwait(false);
            return new Suspension(this);
        }
        catch { lock (_gate) _suspensions--; throw; }
    }
    private sealed class Suspension(DialogueContextLifetime owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) owner._suspensions--;
        }
    }
    public sealed class Lease : IDisposable
    {
        private readonly DialogueContextLifetime _owner;
        private readonly long _generation;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;
        internal Lease(DialogueContextLifetime owner, long generation, CancellationToken cancellationToken)
        {
            _owner = owner; _generation = generation;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Token = _cancellation.Token;
        }
        public CancellationToken Token { get; }
        internal Task Completion => _completion.Task;
        internal void Cancel() { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        public void CheckCurrent()
        {
            lock (_owner._gate)
                if (_disposed || _generation != _owner._generation || Token.IsCancellationRequested)
                    throw new OperationCanceledException(Token);
        }
        public bool TryCommit(Action action)
        {
            lock (_owner._gate)
            {
                if (_disposed || _generation != _owner._generation || Token.IsCancellationRequested) return false;
                action();
                return true;
            }
        }
        public T Commit<T>(Func<T> action)
        {
            T result = default!;
            if (!TryCommit(() => result = action()))
            {
                Cancel();
                throw new OperationCanceledException(Token);
            }
            return result;
        }
        public void Commit(Action action) => Commit(() => { action(); return true; });
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                _owner._active.Remove(this);
            }
            _cancellation.Dispose();
            _completion.TrySetResult();
        }
    }
}
