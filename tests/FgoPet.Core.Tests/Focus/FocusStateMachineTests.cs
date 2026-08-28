using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using Xunit;

namespace FgoPet.Core.Tests.Focus;

public sealed class FocusStateMachineTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T09:00:00Z");

    [Fact]
    public void Start_enters_focusing_with_the_full_focus_budget()
    {
        var idle = FocusSession.Idle;
        var result = FocusStateMachine.Apply(idle, new FocusCommand.Start(FocusPreset.Create(25, 5, 4), "servant-mash"), At);

        Assert.Equal(FocusStatus.Focusing, result.Session.Status);
        Assert.Equal("servant-mash", result.Session.ServantId);
        Assert.Equal(1_500, result.Session.RemainingSeconds);
        Assert.Equal(1, result.Session.CurrentCycle);
        var started = Assert.Single(result.Events);
        Assert.Equal(RuntimeEventType.FocusStarted, started.Type);
    }

    [Fact]
    public void Pause_and_resume_preserve_the_remaining_budget()
    {
        var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
        var progressed = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(100), At.AddSeconds(100)).Session;
        var paused = FocusStateMachine.Apply(progressed, new FocusCommand.Pause(), At.AddSeconds(101)).Session;

        Assert.Equal(FocusStatus.PausedFocus, paused.Status);
        Assert.Equal(1_400, paused.RemainingSeconds);

        var resumed = FocusStateMachine.Apply(paused, new FocusCommand.Resume(), At.AddSeconds(110)).Session;
        Assert.Equal(FocusStatus.Focusing, resumed.Status);
        Assert.Equal(1_400, resumed.RemainingSeconds);
    }

    [Fact]
    public void Focus_completion_emits_one_event_and_starts_break()
    {
        var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
        var result = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(1_500), At.AddMinutes(25));

        Assert.Equal(FocusStatus.Breaking, result.Session.Status);
        Assert.Equal(300, result.Session.RemainingSeconds);
        var completed = Assert.Single(result.Events);
        Assert.Equal(RuntimeEventType.FocusCompleted, completed.Type);
        Assert.Equal("servant-mash", completed.ServantId);
        Assert.Equal(1_500, completed.EffectiveSeconds);
    }

    [Fact]
    public void Stop_during_focus_records_elapsed_but_no_effective_seconds()
    {
        var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
        var progressed = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(720), At.AddMinutes(12)).Session;
        var stopped = FocusStateMachine.Apply(progressed, new FocusCommand.Stop(), At.AddMinutes(12));

        Assert.Equal(FocusStatus.Idle, stopped.Session.Status);
        Assert.Equal(0, Assert.Single(stopped.Events).EffectiveSeconds);
        Assert.Equal(720, stopped.Events[0].ElapsedSeconds);
    }

    [Fact]
    public void Break_completion_advances_the_cycle_and_starts_the_next_focus()
    {
        var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
        var progressed = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(1_500), At.AddMinutes(25)).Session;
        var result = FocusStateMachine.Apply(progressed, new FocusCommand.Elapsed(300), At.AddMinutes(30));

        Assert.Equal(FocusStatus.Focusing, result.Session.Status);
        Assert.Equal(2, result.Session.CurrentCycle);
        Assert.Equal(1_500, result.Session.RemainingSeconds);
        var cycleCompleted = Assert.Single(result.Events);
        Assert.Equal(RuntimeEventType.CycleCompleted, cycleCompleted.Type);
        // The completed focus stage of cycle 1 was the effective stage; the cycle event closes it.
        Assert.Equal(1, cycleCompleted.CycleNumber);
    }

    [Fact]
    public void Final_focus_completion_marks_the_session_completed()
    {
        var preset = FocusPreset.Create(25, 5, 1);
        var started = FocusSession.Start("session-1", "servant-mash", preset, At);
        var result = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(1_500), At.AddMinutes(25));

        Assert.Equal(FocusStatus.Completed, result.Session.Status);
        Assert.Equal(0, result.Session.RemainingSeconds);
        var completed = Assert.Single(result.Events);
        Assert.Equal(RuntimeEventType.FocusCompleted, completed.Type);
    }

    [Fact]
    public void Elapsed_never_crosses_more_than_one_boundary_per_call()
    {
        var preset = FocusPreset.Create(25, 5, 2);
        var started = FocusSession.Start("session-1", "servant-mash", preset, At);
        // Overshoot both the focus and the break in one call: must stop at the first boundary.
        var result = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(10_000), At.AddMinutes(200));

        Assert.Equal(FocusStatus.Breaking, result.Session.Status);
        Assert.Equal(300, result.Session.RemainingSeconds);
        Assert.Single(result.Events);
    }

    [Fact]
    public void RestorePaused_maps_active_states_without_advancing_time()
    {
        var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
        Assert.Equal(FocusStatus.PausedFocus, started.RestorePaused().Status);

        var progressed = FocusStateMachine.Apply(started, new FocusCommand.Elapsed(1_500), At.AddMinutes(25)).Session;
        Assert.Equal(FocusStatus.PausedBreak, progressed.RestorePaused().Status);

        var paused = progressed.RestorePaused();
        Assert.Equal(FocusStatus.PausedBreak, paused.RestorePaused().Status);

        var idle = FocusStateMachine.Apply(
            FocusSession.Start("session-2", "servant-mash", preset, At),
            new FocusCommand.Stop(), At).Session;
        Assert.Equal(idle.Status, idle.RestorePaused().Status);
    }

    [Fact]
    public void Invalid_command_for_a_state_throws()
    {
        var started = FocusSession.Start("session-1", "servant-mash", FocusPreset.Create(25, 5, 4), At);
        Assert.Throws<InvalidOperationException>(() =>
            FocusStateMachine.Apply(started, new FocusCommand.Resume(), At));
    }

    private static readonly FocusPreset preset = FocusPreset.Create(25, 5, 4);
}
