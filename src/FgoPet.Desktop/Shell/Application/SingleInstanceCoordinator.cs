using System.IO;
using System.IO.Pipes;
using System.Text;

namespace FgoPet.App.Lifetime;

/// <summary>
/// Ensures only one pet process is active. The primary instance holds a named mutex and
/// listens on a named pipe; a second instance forwards its command line (typically a
/// <c>.fgopetpack</c> path) to the primary and exits.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Task? _listener;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Attempts to become the primary instance. Returns true and a live coordinator when
    /// this instance wins the mutex; returns false when another instance is already
    /// active so the caller can forward its arguments and exit.
    /// </summary>
    public static bool TryCreatePrimary(string appId, out SingleInstanceCoordinator? coordinator, out bool isPrimary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var mutex = new Mutex(initiallyOwned: true, $"Local\\FgoPet.Single.{appId}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            coordinator = null;
            isPrimary = false;
            return false;
        }

        coordinator = new SingleInstanceCoordinator(mutex, $"FgoPet.Pipe.{appId}");
        isPrimary = true;
        return true;
    }

    public void ListenForActivation(Action<string> onPathReceived, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPathReceived);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) throw new InvalidOperationException("The activation listener is already running.");
            _listener = PipeServerLoopAsync(onPathReceived, cancellationToken);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_disposed) _stopping.Cancel();
            return _listener ?? Task.CompletedTask;
        }
    }

    /// <summary>Secondary instance: forwards a path to the primary and waits for an acknowledgement.</summary>
    public static bool ForwardActivation(string appId, string path, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", $"FgoPet.Pipe.{appId}", PipeDirection.InOut);
            client.Connect((int)timeout.TotalMilliseconds);
            var payload = Encoding.UTF8.GetBytes(path + "\n");
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return client.ReadByte() == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping.Cancel();
            _disposed = true;
            // Normal shutdown has awaited StopAsync. Emergency disposal only cancels the listener.
            _mutex.Dispose();
            if (_listener is null || _listener.IsCompleted) _stopping.Dispose();
            else _ = _listener.ContinueWith(_ => _stopping.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task PipeServerLoopAsync(Action<string> onPathReceived, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                var path = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                pipe.WriteByte(1);
                pipe.Flush();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                onPathReceived(path);
            }
            catch (Exception)
            {
                return; // pipe server interrupted; the process is exiting
            }
        }
    }
}
