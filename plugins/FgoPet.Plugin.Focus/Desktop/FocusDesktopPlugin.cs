using System.Windows.Threading;
using FgoPet.App.Focus;
using FgoPet.Extensibility;
using FgoPet.Core.Focus;
using FgoPet.Core.Events;

namespace FgoPet.Plugin.Focus.Desktop;

/// <summary>The capability owns its cadence. Window creation, hiding and replacement never create another timer.</summary>
public sealed class FocusDesktopPlugin(IFocusSessionService focus, TimeProvider time) : IFgoPetPlugin, ICompanionSignalSource, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private ITimer? _timer;
    private CancellationToken _stopping;
    private int _closed;
    private int _queued;
    private int _started;
    public PluginManifest Manifest { get; } = new("firstparty.focus", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty with { Signals = [this] };
    public event Action<CompanionSignal>? Signal;
    private FocusStatus _lastStatus = FocusStatus.Idle;
    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return ValueTask.CompletedTask;
        _stopping = stoppingToken;
        if (focus is FocusSessionService owner)
        {
            owner.BindLifecycle(stoppingToken);
            _lastStatus = owner.Current.Status;
            owner.TransitionCommitted += OnTransitionCommitted;
        }
        _timer = time.CreateTimer(_ => QueueTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return ValueTask.CompletedTask;
    }
    private void QueueTick()
    {
        if (Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested || _dispatcher.HasShutdownStarted
            || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _dispatcher.BeginInvoke(() =>
        {
            try { if (Volatile.Read(ref _closed) == 0 && !_stopping.IsCancellationRequested) focus.Tick(); }
            finally { Volatile.Write(ref _queued, 0); }
        }, DispatcherPriority.Background);
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var first = Interlocked.Exchange(ref _closed, 1) == 0;
        if (focus is FocusSessionService service) service.TransitionCommitted -= OnTransitionCommitted;
        var timer = Interlocked.Exchange(ref _timer, null);
        if (timer is not null) await timer.DisposeAsync();
        if (first && focus is FocusSessionService owner && owner.Current.Status != FocusStatus.Idle && !_dispatcher.HasShutdownStarted)
        {
            if (_dispatcher.CheckAccess()) owner.Checkpoint();
            else await _dispatcher.InvokeAsync(owner.Checkpoint).Task.ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (focus is FocusSessionService service) service.TransitionCommitted -= OnTransitionCommitted;
        Interlocked.Exchange(ref _timer, null)?.Dispose();
    }

    private void OnTransitionCommitted(FocusTransition transition)
    {
        if (Volatile.Read(ref _closed) != 0 || _stopping.IsCancellationRequested) return;
        var session = transition.Session;
        CompanionSignalKind? kind = transition.Events.FirstOrDefault()?.Type switch
        {
            RuntimeEventType.FocusStarted => CompanionSignalKind.ActivityStarted,
            RuntimeEventType.FocusStopped => CompanionSignalKind.ActivityStopped,
            RuntimeEventType.FocusCompleted when session.Status == FocusStatus.Completed => CompanionSignalKind.ActivityCompleted,
            RuntimeEventType.FocusCompleted or RuntimeEventType.CycleCompleted => CompanionSignalKind.PhaseCompleted,
            _ when session.Status != _lastStatus && session.Status is FocusStatus.PausedFocus or FocusStatus.PausedBreak => CompanionSignalKind.ActivityPaused,
            _ when session.Status != _lastStatus && session.Status is FocusStatus.Focusing or FocusStatus.Breaking => CompanionSignalKind.ActivityStarted,
            _ => null
        };
        _lastStatus = session.Status;
        if (kind is null) return;
        var fact = transition.Events.FirstOrDefault();
        Signal?.Invoke(new(kind.Value, session.ServantId, session.SessionId, session.UpdatedAtUtc,
            fact?.CycleNumber ?? session.CurrentCycle,
            (fact?.Phase ?? session.Phase) == FocusPhase.Break ? CompanionActivityPhase.Rest : CompanionActivityPhase.Work,
            fact?.ElapsedSeconds ?? session.PhaseElapsedSeconds));
    }
}
