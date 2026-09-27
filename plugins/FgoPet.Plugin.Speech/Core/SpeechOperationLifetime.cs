namespace FgoPet.App.Speech;

/// <summary>Fences admission before cancellation and tracks every admitted operation through its cleanup.</summary>
internal sealed class SpeechOperationLifetime
{
    private readonly object _gate = new();
    private readonly HashSet<Operation> _operations = [];
    private Operation? _current;
    private CancellationToken _stopping;
    private bool _closed;

    public void Bind(CancellationToken stoppingToken)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); _stopping = stoppingToken; }
    }
    public void EnsureOpen()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); _stopping.ThrowIfCancellationRequested(); }
    }
    public Operation Begin(CancellationToken cancellationToken)
    {
        Operation? previous;
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _stopping.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            operation = new(this, CancellationTokenSource.CreateLinkedTokenSource(_stopping, cancellationToken));
            _operations.Add(operation);
            previous = _current;
            _current = operation;
        }
        previous?.Cancel();
        return operation;
    }
    public bool IsCurrent(Operation operation)
    {
        lock (_gate) return !_closed && ReferenceEquals(_current, operation) && !operation.Token.IsCancellationRequested;
    }
    public void Stop()
    {
        Operation[] operations;
        lock (_gate) { _current = null; operations = _operations.ToArray(); }
        foreach (var operation in operations) operation.Cancel();
    }
    public Task CloseAndDrainAsync()
    {
        Operation[] operations;
        lock (_gate) { _closed = true; _current = null; operations = _operations.ToArray(); }
        foreach (var operation in operations) operation.Cancel();
        return Task.WhenAll(operations.Select(x => x.Completion.Task));
    }
    private void Complete(Operation operation)
    {
        lock (_gate)
        {
            _operations.Remove(operation);
            if (ReferenceEquals(_current, operation)) _current = null;
        }
        operation.Completion.TrySetResult();
    }
    internal sealed class Operation(SpeechOperationLifetime owner, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token { get; } = source.Token;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Cancel()
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { } // Completion raced cancellation.
            catch (AggregateException) { } // A provider's callback cannot prevent fencing or draining other operations.
        }
        public void Dispose() { source.Dispose(); owner.Complete(this); }
    }
}
