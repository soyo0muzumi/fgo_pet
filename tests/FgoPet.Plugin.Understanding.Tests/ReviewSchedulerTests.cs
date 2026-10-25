using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewSchedulerTests
{
    private readonly ConversationScope _scope=new("mash","p");
    private readonly ReviewWorkQueueTests.Timeline _sources=new();
    private readonly ReviewServiceTests.Models _models=new();
    private readonly Clock _clock=new();
    private readonly UnderstandingService _owner;
    private readonly UnderstandingReviewScheduler _scheduler;
    public ReviewSchedulerTests()
    {
        var db=new RuntimeDatabase(Path.Combine(Path.GetTempPath(),"fgo-scheduler-"+Guid.NewGuid().ToString("N"),"runtime.db"),pooling:false);
        new RuntimeDatabaseMigrator(db).Migrate();_owner=new(db,_sources,_clock);
        _scheduler=new(_owner,_sources,new(_owner,_sources,_sources,_models,new DialogueContextLifetime(),_clock),_clock);
    }
    private void Enable()=>_owner.Configure(_scope,0,new(UnderstandingMode.Proposals,ReadSources:true,ExternalProcessing:true,SaveCandidates:true));
    [Fact] public async Task A_large_backlog_does_not_dispatch_again_on_every_five_second_tick()
    {
        Enable();_sources.Add(20);_clock.Advance(120);await _scheduler.PulseAsync();
        Assert.Equal(1,_models.Dispatches);
        _clock.Advance(5);await _scheduler.PulseAsync();Assert.Equal(1,_models.Dispatches);
        _clock.Advance(115);await _scheduler.PulseAsync();Assert.Equal(2,_models.Dispatches);
    }
    [Fact] public async Task Idle_uses_monotonic_time_and_wall_clock_jumps_do_not_trigger_review()
    {
        Enable();_sources.Add(2);await _scheduler.PulseAsync();Assert.Equal(0,_models.Dispatches);
        _clock.Utc=_clock.Utc.AddDays(365);await _scheduler.PulseAsync();Assert.Equal(0,_models.Dispatches);
        _clock.Advance(119);await _scheduler.PulseAsync();Assert.Equal(0,_models.Dispatches);
        _clock.Advance(1);await _scheduler.PulseAsync();Assert.Equal(1,_models.Dispatches);
    }
    [Fact] public async Task Six_completed_turns_are_registered_without_notification_and_foreground_lifetime_is_not_reused()
    {
        Enable();_sources.Add(12);await _scheduler.PulseAsync();Assert.Equal(1,_models.Dispatches);
        Assert.True(_owner.WorkSummary(_scope).PendingSources>0); // Bound batch size preserves the remainder.
        _clock.Advance(120);await _scheduler.PulseAsync();Assert.Equal(2,_models.Dispatches);
        Assert.Equal(0,_owner.WorkSummary(_scope).PendingSources);
    }
    [Fact] public async Task Off_has_zero_dispatch_at_every_threshold_and_new_grant_starts_after_old_sources()
    {
        _sources.Add(20,1000);_clock.Advance(1000);await _scheduler.PulseAsync();Assert.Equal(0,_models.Dispatches);
        _owner.Configure(_scope,0,new(UnderstandingMode.Proposals,ReadSources:true,ExternalProcessing:true,SaveCandidates:true),20);
        await _scheduler.PulseAsync();Assert.Equal(0,_models.Dispatches);
        _sources.Add(2);await _scheduler.PulseAsync();Assert.Equal(1,_models.Dispatches);
        Assert.All(Assert.Single(_owner.List(_scope,true)).Sources,s=>Assert.True(s.SourceRevision>20));
    }
    [Fact] public async Task Oldest_range_eventually_runs_under_continuous_small_turn_notifications()
    {
        Enable();_sources.Add(1);await _scheduler.PulseAsync();
        for(var i=0;i<14;i++){_clock.Advance(60);_scheduler.Observe(new(new("c","mash","p"),"u","Synthetic","a","Synthetic",null,null,default));await _scheduler.PulseAsync();}
        Assert.Equal(0,_models.Dispatches);
        _clock.Advance(60);_scheduler.Observe(new(new("c","mash","p"),"u","Synthetic","a","Synthetic",null,null,default));
        await _scheduler.PulseAsync();Assert.Equal(1,_models.Dispatches);
    }
    [Fact] public async Task Disposal_also_cancels_and_drains_an_explicit_pulse_before_periodic_start()
    {
        Enable();_sources.Add(2);_clock.Advance(120);_models.WaitForCancellation=true;
        using var cancellation=new CancellationTokenSource();var pulse=_scheduler.PulseAsync(cancellation.Token);
        await _models.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var drained=pulse.IsCompleted;cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pulse);
        Assert.True(drained,"Disposal returned while its explicit review was still running.");
        Assert.Equal(0,_models.Dispatches);Assert.Equal(2,_owner.WorkSummary(_scope).PendingSources);
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Utc=DateTimeOffset.Parse("2026-10-20T00:00:00Z");private long _ticks;
        public override DateTimeOffset GetUtcNow()=>Utc;
        public override long GetTimestamp()=>_ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public void Advance(int seconds){_ticks+=TimeSpan.FromSeconds(seconds).Ticks;Utc=Utc.AddSeconds(seconds);}
    }
}
