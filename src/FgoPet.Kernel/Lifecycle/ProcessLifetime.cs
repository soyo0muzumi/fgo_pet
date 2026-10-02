namespace FgoPet.Kernel.Lifecycle;

public interface IProcessLifetime
{
    CancellationToken StoppingToken { get; }
    bool IsStopping { get; }
}

public sealed class ProcessLifetime : IProcessLifetime, IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _token;
    private bool _isStopping;
    private bool _disposed;
    private bool _cancelling;

    public ProcessLifetime() => _token = _stopping.Token;
    public CancellationToken StoppingToken => _token;
    public bool IsStopping { get { lock (_gate) return _isStopping; } }
    internal bool TryAdmit(Action admit)
    {
        lock (_gate)
        {
            if (_isStopping) return false;
            admit();
            return true;
        }
    }
    public void RequestStop()
    {
        lock (_gate)
        {
            if (_isStopping) return;
            _isStopping = true;
            _cancelling = true;
        }
        // Callbacks run after the admission fence closes, outside its lock.
        try { _stopping.Cancel(); }
        catch (AggregateException) { CancellationCallbackFailed = true; }
        finally
        {
            lock (_gate)
            {
                _cancelling = false;
                if (_disposed) _stopping.Dispose();
            }
        }
    }
    public bool CancellationCallbackFailed { get; private set; }
    public void Dispose()
    {
        RequestStop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_cancelling) _stopping.Dispose();
        }
    }
}
