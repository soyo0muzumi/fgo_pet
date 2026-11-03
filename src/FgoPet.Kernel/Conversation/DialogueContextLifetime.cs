using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>Owner-local cancellation and commit fence; invalidation waits for the old leases to drain.</summary>
public sealed class DialogueContextLifetime : IDialogueContextLifetime, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Lease> _active = [];
    private long _generation;
    private bool _disposed;
    private readonly Coordination _coordination;
    private readonly CancellationToken _processStopping;
    public DialogueContextLifetime(CancellationToken processStopping = default) : this(processStopping, new Coordination()) { }
    private DialogueContextLifetime(CancellationToken processStopping, Coordination coordination)
    {
        _processStopping = processStopping; _coordination = coordination;
        lock (coordination.Gate) coordination.Owners.Add(new(this));
    }
    /// <summary>Local cancellation is independent; maintenance and foreground/background admission stay shared.</summary>
    public DialogueContextLifetime CreateIndependentOwner() => new(_processStopping, _coordination);
    private sealed class Coordination
    {
        internal object Gate { get; } = new();
        internal List<WeakReference<DialogueContextLifetime>> Owners { get; } = [];
        internal int Suspensions;
        internal DialogueContextLifetime[] LiveOwners()
        {
            var live = new List<DialogueContextLifetime>();
            for (var index = Owners.Count - 1; index >= 0; index--)
            {
                if (Owners[index].TryGetTarget(out var owner)) live.Add(owner);
                else Owners.RemoveAt(index);
            }
            return live.ToArray();
        }
    }
    public long Generation { get { lock (_gate) return _generation; } }
    public IDialogueBackgroundLease? TryAcquireBackground(CancellationToken cancellationToken)
    {
        lock (_coordination.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();_processStopping.ThrowIfCancellationRequested();
            if (_coordination.Suspensions > 0 || _disposed || _coordination.LiveOwners().Any(owner =>
                { lock (owner._gate) return owner._active.Count > 0; })) return null;
            lock (_gate)
            {
                var lease=new Lease(this,_generation,cancellationToken,_processStopping,background:true);
                _active.Add(lease);return lease;
            }
        }
    }
    public Lease Acquire(CancellationToken cancellationToken)
    {
        Lease lease;Lease[] reviews;
        lock (_coordination.Gate)
        {
            _processStopping.ThrowIfCancellationRequested();
            if (_coordination.Suspensions > 0 || _disposed) throw new OperationCanceledException("Dialogue maintenance is in progress.", cancellationToken);
            lock (_gate)
            {
                lease = new Lease(this, _generation, cancellationToken, _processStopping);
                _active.Add(lease);
            }
            reviews = _coordination.LiveOwners().SelectMany(owner =>
                { lock (owner._gate) return owner._active.Where(item => item.IsBackground).ToArray(); }).ToArray();
        }
        // Acquiring the foreground fence is immediate. User cancellation callbacks never run under this lock.
        foreach(var review in reviews)review.CancelBackground();
        return lease;
    }
    private Lease[] CancelOld()
    {
        Lease[] active;
        lock (_gate) { _generation++; active = _active.ToArray(); }
        foreach (var lease in active)
            if(lease.IsBackground)lease.CancelBackground();else lease.Cancel();
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
        DialogueContextLifetime[] owners;
        lock (_coordination.Gate) { _coordination.Suspensions++; owners = _coordination.LiveOwners(); }
        try
        {
            await Task.WhenAll(owners.Select(owner => owner.InvalidateAsync(cancellationToken))).ConfigureAwait(false);
            return new Suspension(_coordination);
        }
        catch { lock (_coordination.Gate) _coordination.Suspensions--; throw; }
    }
    public void Dispose()
    {
        lock (_coordination.Gate) _disposed = true;
        Invalidate();
    }
    private sealed class Suspension(Coordination coordination) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (coordination.Gate) coordination.Suspensions--;
        }
    }
    public sealed class Lease : IDialogueBackgroundLease
    {
        private readonly DialogueContextLifetime _owner;
        private readonly long _generation;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;
        internal Lease(DialogueContextLifetime owner, long generation, CancellationToken cancellationToken, CancellationToken processStopping, bool background=false)
        {
            _owner = owner; _generation = generation;
            IsBackground=background;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, processStopping);
            Token = _cancellation.Token;
        }
        public CancellationToken Token { get; }
        internal bool IsBackground { get; }
        internal Task Completion => _completion.Task;
        internal void Cancel() { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        internal void CancelBackground()
        {
            try { _=ObserveCancellation(_cancellation.CancelAsync()); }
            catch(ObjectDisposedException) { }
        }
        private static async Task ObserveCancellation(Task cancellation)
        {try{await cancellation.ConfigureAwait(false);}catch(Exception){/* A throwing callback cannot reopen the foreground fence. */}}
        public void CheckCurrent()
        {
            lock (_owner._gate)
                if (_disposed || _generation != _owner._generation || Token.IsCancellationRequested ||
                    IsBackground && _owner._active.Any(lease=>!lease.IsBackground))
                    throw new OperationCanceledException(Token);
        }
        public bool TryCommit(Action action)
        {
            lock (_owner._gate)
            {
                if (_disposed || _generation != _owner._generation || Token.IsCancellationRequested ||
                    IsBackground && _owner._active.Any(lease=>!lease.IsBackground)) return false;
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
