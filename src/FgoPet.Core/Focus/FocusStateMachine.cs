using FgoPet.Core.Events;

namespace FgoPet.Core.Focus;

/// <summary>
/// Pure deterministic transition engine. <see cref="Apply"/> receives explicit
/// timestamps; <see cref="TimeProvider"/> lives only at the App boundary.
/// </summary>
public static class FocusStateMachine
{
    public static FocusTransition Apply(FocusSession session, FocusCommand command, DateTimeOffset occurredAtUtc) =>
        (session.Status, command) switch
        {
            (FocusStatus.Idle, FocusCommand.Start start) => Start(session, start, occurredAtUtc),
            (FocusStatus.Focusing, FocusCommand.Pause) => PauseFocus(session, occurredAtUtc),
            (FocusStatus.PausedFocus, FocusCommand.Resume) => ResumeFocus(session, occurredAtUtc),
            (FocusStatus.Breaking, FocusCommand.Pause) => PauseBreak(session, occurredAtUtc),
            (FocusStatus.PausedBreak, FocusCommand.Resume) => ResumeBreak(session, occurredAtUtc),
            (FocusStatus.Focusing or FocusStatus.Breaking or FocusStatus.PausedFocus or FocusStatus.PausedBreak,
                FocusCommand.Stop) => Stop(session, occurredAtUtc),
            (FocusStatus.Focusing or FocusStatus.Breaking, FocusCommand.Elapsed elapsed) =>
                Advance(session, elapsed.Seconds, occurredAtUtc),
            (FocusStatus.Completed, FocusCommand.Acknowledge) => FocusTransition.WithoutEvents(FocusSession.Idle),
            _ => throw new InvalidOperationException($"Command {command.GetType().Name} is invalid for {session.Status}."),
        };

    private static FocusTransition Start(FocusSession previous, FocusCommand.Start start, DateTimeOffset at)
    {
        var preset = start.Preset;
        var session = FocusSession.Start(
            previous.SessionId.Length == 0 ? NewSessionId(at) : previous.SessionId,
            start.ServantId,
            preset,
            at);
        var events = new List<FocusEventDraft>
        {
            new(RuntimeEventType.FocusStarted, start.ServantId, 1, FocusPhase.Focus, 0, 0, at, $"ev-{session.SessionId}-start"),
        };
        return new(session, events);
    }

    private static FocusTransition PauseFocus(FocusSession session, DateTimeOffset at) =>
        FocusTransition.WithoutEvents(session with { Status = FocusStatus.PausedFocus, UpdatedAtUtc = at });

    private static FocusTransition ResumeFocus(FocusSession session, DateTimeOffset at) =>
        FocusTransition.WithoutEvents(session with { Status = FocusStatus.Focusing, UpdatedAtUtc = at });

    private static FocusTransition PauseBreak(FocusSession session, DateTimeOffset at) =>
        FocusTransition.WithoutEvents(session with { Status = FocusStatus.PausedBreak, UpdatedAtUtc = at });

    private static FocusTransition ResumeBreak(FocusSession session, DateTimeOffset at) =>
        FocusTransition.WithoutEvents(session with { Status = FocusStatus.Breaking, UpdatedAtUtc = at });

    private static FocusTransition Stop(FocusSession session, DateTimeOffset at)
    {
        var stopped = session with
        {
            Status = FocusStatus.Idle,
            IsCurrent = false,
            UpdatedAtUtc = at,
        };
        var events = new List<FocusEventDraft>
        {
            new(
                RuntimeEventType.FocusStopped,
                session.ServantId,
                session.CurrentCycle,
                session.Phase,
                session.PhaseElapsedSeconds,
                EffectiveSeconds: 0,
                at,
                $"ev-{session.SessionId}-stop"),
        };
        return new(stopped, events);
    }

    /// <summary>
    /// Subtracts whole seconds from the current phase and emits exactly one boundary
    /// event per call; excess seconds never cross more than one boundary.
    /// </summary>
    private static FocusTransition Advance(FocusSession session, int seconds, DateTimeOffset at)
    {
        if (seconds <= 0)
        {
            return FocusTransition.WithoutEvents(session);
        }

        var remaining = session.RemainingSeconds - seconds;
        if (remaining > 0)
        {
            return FocusTransition.WithoutEvents(session with
            {
                RemainingSeconds = remaining,
                PhaseElapsedSeconds = session.PhaseElapsedSeconds + seconds,
                UpdatedAtUtc = at,
            });
        }

        // Boundary reached: clamp elapsed to the budget and advance exactly one phase.
        var consumed = session.RemainingSeconds;
        if (session.Phase == FocusPhase.Focus)
        {
            return CompleteFocus(session, consumed, at);
        }

        return CompleteBreak(session, consumed, at);
    }

    private static FocusTransition CompleteFocus(FocusSession session, int consumed, DateTimeOffset at)
    {
        var isFinalPhase = session.CurrentCycle >= session.TotalCycles;
        var next = isFinalPhase
            ? session with
            {
                Status = FocusStatus.Completed,
                Phase = FocusPhase.Focus,
                RemainingSeconds = 0,
                PhaseElapsedSeconds = session.PhaseElapsedSeconds + consumed,
                UpdatedAtUtc = at,
            }
            : session with
            {
                Status = FocusStatus.Breaking,
                Phase = FocusPhase.Break,
                RemainingSeconds = session.BreakSeconds,
                PhaseElapsedSeconds = 0,
                UpdatedAtUtc = at,
            };
        var events = new List<FocusEventDraft>
        {
            new(
                RuntimeEventType.FocusCompleted,
                session.ServantId,
                session.CurrentCycle,
                FocusPhase.Focus,
                session.PhaseElapsedSeconds + consumed,
                EffectiveSeconds: consumed,
                at,
                $"ev-{session.SessionId}-focus-{session.CurrentCycle}"),
        };
        return new(next, events);
    }

    private static FocusTransition CompleteBreak(FocusSession session, int consumed, DateTimeOffset at)
    {
        var next = session with
        {
            Status = FocusStatus.Focusing,
            Phase = FocusPhase.Focus,
            CurrentCycle = session.CurrentCycle + 1,
            RemainingSeconds = session.FocusSeconds,
            PhaseElapsedSeconds = 0,
            UpdatedAtUtc = at,
        };
        var events = new List<FocusEventDraft>
        {
            new(
                RuntimeEventType.CycleCompleted,
                session.ServantId,
                session.CurrentCycle,
                FocusPhase.Break,
                session.PhaseElapsedSeconds + consumed,
                EffectiveSeconds: 0,
                at,
                $"ev-{session.SessionId}-break-{session.CurrentCycle}"),
        };
        return new(next, events);
    }

    private static string NewSessionId(DateTimeOffset at) =>
        $"session-{at:yyyyMMddTHHmmssfffZ}";
}
