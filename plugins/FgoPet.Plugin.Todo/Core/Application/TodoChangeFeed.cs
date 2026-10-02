namespace FgoPet.App.Services;

/// <summary>A process-local invalidation hint; revision is not a database version.</summary>
public sealed record TodoChange(long Revision, IReadOnlyList<string>? AffectedIds = null);

public sealed record TodoRevisionSnapshot<T>(long Revision, T Value);

/// <summary>Observes the authoritative Todo service without adding another change bus or store.</summary>
public sealed class TodoChangeFeed : IDisposable
{
    private readonly TodoApplicationService _todos;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private long _revision;
    private bool _disposed;

    public TodoChangeFeed(TodoApplicationService todos)
    {
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
        _todos.Changed += OnServiceChanged;
    }

    public long Revision => Interlocked.Read(ref _revision);
    internal int ActiveSubscriptionCount
    {
        get { lock (_gate) return _subscriptions.Count; }
    }

    public IDisposable Subscribe(Action<TodoChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, observer);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    /// <summary>Call after Subscribe; retry if a write occurs during the read.</summary>
    public TodoRevisionSnapshot<T> ReadStableSnapshot<T>(Func<T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            var before = Revision;
            var value = read();
            var after = Revision;
            if (before == after) return new(after, value);
        }

        throw new InvalidOperationException("TODO_SNAPSHOT_BUSY");
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _todos.Changed -= OnServiceChanged;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    private void OnServiceChanged()
    {
        Subscription[] subscriptions;
        TodoChange change;
        lock (_gate)
        {
            if (_disposed) return;
            change = new TodoChange(Interlocked.Increment(ref _revision));
            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions) subscription.Publish(change);
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate) _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(TodoChangeFeed owner, Action<TodoChange> observer) : IDisposable
    {
        private readonly object _gate = new();
        private bool _disposed;

        public void Publish(TodoChange change)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { observer(change); } catch (Exception) { /* An observer cannot undo a committed write. */ }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            owner.Remove(this);
        }
    }
}
