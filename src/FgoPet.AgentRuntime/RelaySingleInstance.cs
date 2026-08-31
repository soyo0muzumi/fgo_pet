namespace FgoPet.AgentRuntime;

/// <summary>
/// Holds the relay mutex on a dedicated thread so release remains valid when an
/// async host disposes the owner from a continuation thread.
/// </summary>
public sealed class RelaySingleInstance : IDisposable
{
    private readonly ManualResetEventSlim _initialized = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly string _name;
    private readonly Thread _ownerThread;
    private Mutex? _mutex;
    private Exception? _initializationError;
    private int _isOwner;
    private int _disposed;

    private RelaySingleInstance(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _ownerThread = new Thread(OwnershipLoop)
        {
            IsBackground = true,
            Name = "FgoPet relay mutex owner",
        };
        _ownerThread.Start();
        _initialized.Wait();
        if (_initializationError is not null)
        {
            _ownerThread.Join();
            _initialized.Dispose();
            _stop.Dispose();
            throw new InvalidOperationException("The relay single-instance mutex could not be created.", _initializationError);
        }
    }

    public string Name => _name;
    public bool IsOwner => Volatile.Read(ref _isOwner) != 0;

    public static RelaySingleInstance TryAcquire(string name) => new(name);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        if (Environment.CurrentManagedThreadId != _ownerThread.ManagedThreadId)
        {
            _ownerThread.Join();
        }

        _initialized.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OwnershipLoop()
    {
        Mutex? mutex = null;
        try
        {
            // initiallyOwned is important for a newly-created mutex. If the
            // named object already exists, the constructor does not block; a
            // non-blocking WaitOne handles an unowned existing object.
            mutex = new Mutex(initiallyOwned: true, _name, out var createdNew);
            _mutex = mutex;
            var owns = createdNew;
            if (!owns)
            {
                try
                {
                    owns = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    owns = true;
                }
            }

            if (!owns)
            {
                _initialized.Set();
                return;
            }

            Volatile.Write(ref _isOwner, 1);
            _initialized.Set();
            _stop.Wait();
            mutex.ReleaseMutex();
            Volatile.Write(ref _isOwner, 0);
        }
        catch (Exception error)
        {
            _initializationError = error;
            _initialized.Set();
        }
        finally
        {
            Volatile.Write(ref _isOwner, 0);
            mutex?.Dispose();
            _mutex = null;
            _initialized.Set();
        }
    }
}
