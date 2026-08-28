using FgoPet.App.Focus;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Focus;

namespace FgoPet.App.Focus;

/// <summary>
/// TimeProvider-driven orchestration. On <see cref="Tick"/> the elapsed whole seconds
/// come from <see cref="TimeProvider.GetElapsedTime"/> between command timestamps;
/// the sub-second remainder is retained by advancing the stored baseline only by the
/// consumed whole seconds. Completion boundaries commit through
/// <see cref="SqliteFocusCompletionUnit"/>; every other transition saves a snapshot.
/// </summary>
public sealed class FocusSessionService : IFocusSessionService
{
    /// <summary>Persisted snapshots are capped at one per 30 consumed seconds while running.</summary>
    public const int SnapshotCadenceSeconds = 30;

    private readonly TimeProvider _time;
    private readonly IFocusSnapshotStore _snapshots;
    private readonly SqliteFocusCompletionUnit _completion;
    private long _baselineTimestamp;
    private int _secondsSinceSnapshot;
    private bool _persistenceBlocked;

    public FocusSessionService(TimeProvider time, IFocusSnapshotStore snapshots, SqliteFocusCompletionUnit completion)
    {
        _time = time;
        _snapshots = snapshots;
        _completion = completion;
        Current = FocusSession.Idle;
    }

    public FocusSession Current { get; private set; }

    public event EventHandler? SnapshotChanged;

    public event EventHandler? PersistenceFailed;

    public void Start(FocusPreset preset, string servantId)
    {
        var result = FocusStateMachine.Apply(
            Current.Status == FocusStatus.Idle || Current.Status == FocusStatus.Completed
                ? FocusSession.Idle
                : Current,
            new FocusCommand.Start(preset, servantId),
            _time.GetUtcNow());
        Current = result.Session;
        _baselineTimestamp = _time.GetTimestamp();
        _secondsSinceSnapshot = 0;
        _persistenceBlocked = false;
        ApplyTransition(result, isUserCommand: true);
    }

    public void Pause() => ApplyUserCommand(new FocusCommand.Pause());

    public void Resume()
    {
        var result = FocusStateMachine.Apply(Current, new FocusCommand.Resume(), _time.GetUtcNow());
        Current = result.Session;
        _baselineTimestamp = _time.GetTimestamp();
        _persistenceBlocked = false;
        ApplyTransition(result, isUserCommand: true);
    }

    public void Stop() => ApplyUserCommand(new FocusCommand.Stop());

    public void Tick()
    {
        if (Current.Status is not (FocusStatus.Focusing or FocusStatus.Breaking))
        {
            return;
        }

        var nowTimestamp = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_baselineTimestamp, nowTimestamp);
        var wholeSeconds = (int)elapsed.TotalSeconds;
        if (wholeSeconds <= 0)
        {
            return;
        }

        var result = FocusStateMachine.Apply(Current, new FocusCommand.Elapsed(wholeSeconds), _time.GetUtcNow());
        Current = result.Session;
        _baselineTimestamp += wholeSeconds * TimeSpan.TicksPerSecond;
        _secondsSinceSnapshot += wholeSeconds;
        ApplyTransition(result, isUserCommand: false);
    }

    public void Restore()
    {
        var stored = _snapshots.LoadCurrent();
        if (stored is null)
        {
            Current = FocusSession.Idle;
        }
        else
        {
            Current = stored.RestorePaused();
        }
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyUserCommand(FocusCommand command)
    {
        var result = FocusStateMachine.Apply(Current, command, _time.GetUtcNow());
        Current = result.Session;
        _baselineTimestamp = _time.GetTimestamp();
        _secondsSinceSnapshot = 0;
        ApplyTransition(result, isUserCommand: true);
    }

    private void ApplyTransition(FocusTransition result, bool isUserCommand)
    {
        var boundary = result.Events.FirstOrDefault(draft => draft.Type == RuntimeEventType.FocusCompleted);
        if (boundary is not null)
        {
            var runtimeEvent = boundary.ToRuntimeEvent(result.Session.SessionId, priority: 2);
            try
            {
                _completion.CompleteFocus(result.Session, runtimeEvent);
                _secondsSinceSnapshot = 0;
                _persistenceBlocked = false;
            }
            catch (Exception)
            {
                PauseInMemoryAfterPersistenceFailure();
            }

            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var isRunning = Current.Status is FocusStatus.Focusing or FocusStatus.Breaking;
        var shouldSave = isUserCommand || !isRunning || _secondsSinceSnapshot >= SnapshotCadenceSeconds;
        if (!shouldSave)
        {
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            _snapshots.SaveSnapshot(Current);
            _secondsSinceSnapshot = 0;
            _persistenceBlocked = false;
        }
        catch (Exception)
        {
            PauseInMemoryAfterPersistenceFailure();
        }

        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PauseInMemoryAfterPersistenceFailure()
    {
        Current = Current.RestorePaused();
        _persistenceBlocked = true;
        PersistenceFailed?.Invoke(this, EventArgs.Empty);
    }
}
