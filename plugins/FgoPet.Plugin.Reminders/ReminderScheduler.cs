using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Dialogue;

namespace FgoPet.Plugin.Reminders;

public sealed class ReminderScheduler(ReminderService owner, IReminderNotifier notifier,
    IDialogueContextLifetime lifetime, TimeProvider clock, Action<ReminderDiagnostic>? diagnostics = null) : IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private Task? _stop;
    private CancellationTokenRegistration _processStop;

    public void Start(CancellationToken processStopping)
    {
        lock (_gate)
        {
            if (_stop is not null) throw new InvalidOperationException("REMINDER_SCHEDULER_STOPPED");
            if (_loop is not null) throw new InvalidOperationException("REMINDER_SCHEDULER_STARTED");
            processStopping.ThrowIfCancellationRequested();
            owner.RecoverInterrupted();
            // Publish the worker before registering: an already-cancelling process token
            // may invoke Dispose inline, and its retained drain must include this worker.
            _loop = Task.Run(RunAsync);
            _processStop = processStopping.Register(Dispose);
        }
    }

    public async Task PulseAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        token = linked.Token;
        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // This existing lease gives private-data deletion a cancel-and-drain fence.
            // Foreground activity can defer delivery; it never causes a new model call.
            using var lease = lifetime.TryAcquireBackground(token);
            if (lease is null) return;
            for (var i = 0; i < 20; i++)
            {
                lease.Token.ThrowIfCancellationRequested();
                var reminder = owner.ClaimNext();
                if (reminder is null) break;
                Record(reminder, "claimed");
                var attempted = false;
                try
                {
                    lease.Token.ThrowIfCancellationRequested();
                    attempted = true;
                    var outcome = await notifier.NotifyAsync(reminder, lease.Token).ConfigureAwait(false);
                    var status = outcome switch
                    {
                        ReminderNotificationOutcome.Submitted => ReminderStatus.Submitted,
                        ReminderNotificationOutcome.NotAttempted => ReminderStatus.Pending,
                        _ => ReminderStatus.Failed
                    };
                    var code = outcome == ReminderNotificationOutcome.Unavailable ? "REMINDER_NOTIFICATION_UNAVAILABLE" : null;
                    owner.Finish(reminder, status, code); Record(reminder, status.ToString(), code);
                    if (outcome == ReminderNotificationOutcome.NotAttempted) break;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    var status = attempted ? ReminderStatus.Unknown : ReminderStatus.Pending;
                    var code = attempted ? "REMINDER_DELIVERY_UNKNOWN" : null;
                    owner.Finish(reminder, status, code); Record(reminder, status.ToString(), code);
                    if (error is OperationCanceledException) throw;
                }
            }
        }
        finally { _serial.Release(); }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            do
            {
                try { await PulseAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (_shutdown.IsCancellationRequested) break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Record(null, "worker_failed", "REMINDER_WORKER_FAILED"); }
            } while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_stop ??= StopAsync());
    }
    // Emergency synchronous container teardown cancels without blocking the UI.
    // Normal application shutdown awaits the same retained drain through DisposeAsync.
    public void Dispose()
    {
        lock (_gate) _stop ??= StopAsync();
    }
    private async Task StopAsync()
    {
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A throwing cancellation callback does not establish that dispatch stopped.
            // Keep draining the same worker/pulse before allowing private-state cleanup.
            Record(null, "cancellation_failed", "REMINDER_CANCELLATION_FAILED");
        }
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
        await _processStop.DisposeAsync().ConfigureAwait(false);
    }
    private void Record(Reminder? item, string stage, string? code = null)
    {
        try
        {
            var correlation = item is null ? "worker" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id)))[..24];
            diagnostics?.Invoke(new(clock.GetUtcNow(), correlation, stage, code));
        }
        catch (Exception) { /* Diagnostics cannot turn a submitted notification into a retry. */ }
    }
}
