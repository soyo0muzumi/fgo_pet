using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewServiceTests
{
    private readonly RuntimeDatabase _db=new(Path.Combine(Path.GetTempPath(),"fgo-service-"+Guid.NewGuid().ToString("N"),"runtime.db"),pooling:false);
    private readonly ConversationScope _scope=new("mash","p");
    private readonly ReviewWorkQueueTests.Timeline _sources=new();
    private readonly UnderstandingService _owner;
    private readonly Models _models=new();
    private readonly DialogueContextLifetime _lifetime=new();
    private UnderstandingReviewService Service=>new(_owner,_sources,_sources,_models,_lifetime,TimeProvider.System);
    public ReviewServiceTests(){new RuntimeDatabaseMigrator(_db).Migrate();_owner=new(_db,_sources);}
    private void Configure(UnderstandingMode mode)=>_owner.Configure(_scope,_owner.State(_scope).Revision,
        new(mode,ReadSources:true,SaveCandidates:mode!=UnderstandingMode.Off,ExternalProcessing:true,UseInDialogue:true),_sources.Boundary(_scope));
    [Fact] public async Task Actual_service_reads_supplied_coverage_and_pending_is_not_automatically_resent()
    {
        Configure(UnderstandingMode.Proposals);_sources.Add(2);_owner.Accumulate(_sources,_scope);
        Assert.True(await Service.ProcessNextAsync(_scope));Assert.Equal(1,_models.Dispatches);
        var pending=Assert.Single(_owner.List(_scope,true));Assert.Equal(2,pending.Coverage!.Count);Assert.Empty(_owner.Query(_scope,"").Items);
        _owner.RecoverWork();Assert.False(await Service.ProcessNextAsync(_scope));Assert.Equal(1,_models.Dispatches);
    }
    [Fact] public async Task Revoking_during_async_preparation_refuses_the_handoff()
    {
        Configure(UnderstandingMode.Proposals);_sources.Add(2);_owner.Accumulate(_sources,_scope);
        _models.BeforeDispatch=()=>_owner.Configure(_scope,_owner.State(_scope).Revision,new());
        Assert.True(await Service.ProcessNextAsync(_scope));Assert.Equal(0,_models.Dispatches);Assert.Empty(_owner.List(_scope,true));
    }
    [Fact] public async Task Off_manual_review_dispatches_once_and_restart_does_not_replay_or_store_body()
    {
        Configure(UnderstandingMode.Off);_sources.Add(2);
        var receipt=await Service.ManualAsync(_scope,"explicit-preview",_sources.Heads);
        Assert.All(receipt.Items,i=>Assert.Equal(UnderstandingOutcome.ReviewedNotPersisted,i.Outcome));Assert.Equal(1,_models.Dispatches);
        var replay=await Service.ManualAsync(_scope,"explicit-preview",_sources.Heads);Assert.Equal(receipt.Items,replay.Items);Assert.Equal(1,_models.Dispatches);
        var restarted=new UnderstandingService(_db,_sources);restarted.StartSession();restarted.RecoverWork();
        Assert.Empty(restarted.List(_scope,true));Assert.Null(restarted.ClaimWork(_scope));
    }
    [Fact] public async Task A_foreground_turn_and_missing_budget_leave_the_original_queue_pending()
    {
        Configure(UnderstandingMode.Proposals);_sources.Add(2);_owner.Accumulate(_sources,_scope);
        using(var foreground=_lifetime.Acquire(default))Assert.False(await Service.ProcessNextAsync(_scope));
        _models.Allowed=false;Assert.False(await Service.ProcessNextAsync(_scope));Assert.Equal(0,_models.Dispatches);
        Assert.Equal(2,_owner.WorkSummary(_scope).PendingSources);
    }
    [Fact] public async Task A_committed_receipt_is_recovered_before_new_input_can_create_another_review()
    {
        Configure(UnderstandingMode.Proposals);_sources.Add(2);_owner.Accumulate(_sources,_scope);
        var batch=_owner.ClaimWork(_scope,2);Assert.NotNull(batch);
        var roots=batch.Items.Select(i=>i.Head.Source).ToArray();
        var ticket=_owner.Begin(_scope,batch.Id,roots,coverage:batch.Items.Select(i=>new UnderstandingSourceCoverage(i.Head.Source,0,i.IncludedScalars,i.Head.TotalScalars)).ToArray());
        _owner.Apply(ticket,[new("committed-before-crash",UnderstandingDomain.Self,UnderstandingKind.CurrentInterest,"assistant","Synthetic","Today in this project",roots)]);
        _sources.Add(2);_owner.RecoverWork();
        Assert.True(await Service.ProcessNextAsync(_scope));Assert.Equal(0,_models.Dispatches);
        Assert.Single(_owner.List(_scope,true));
    }
    [Fact] public async Task Returned_off_preview_does_not_claim_that_the_ui_has_confirmed_display()
    {
        Configure(UnderstandingMode.Off);_sources.Add(2);await Service.ManualAsync(_scope,"not-yet-displayed",_sources.Heads);
        Assert.Equal("Reserved",Assert.Single(_owner.AttemptStates(_scope)).Status);
        Service.ConfirmManualPreviewDisplayed(_scope,"not-yet-displayed");
        Assert.Equal("ReviewedNotPersisted",Assert.Single(_owner.AttemptStates(_scope)).Status);
    }
    [Fact] public async Task Continuous_input_can_invalidate_old_work_repeatedly_then_drain_after_input_stops()
    {
        Configure(UnderstandingMode.Proposals);_sources.Add(2);_owner.Accumulate(_sources,_scope);
        _models.BeforeDispatch=()=>_sources.Add(1);
        for(var i=0;i<3;i++){Assert.True(await Service.ProcessNextAsync(_scope));_owner.Accumulate(_sources,_scope);}
        Assert.Equal(0,_models.Dispatches);Assert.Equal(5,_owner.WorkSummary(_scope).PendingSources);
        _models.BeforeDispatch=null;Assert.True(await Service.ProcessNextAsync(_scope));Assert.Equal(1,_models.Dispatches);
        Assert.Equal(0,_owner.WorkSummary(_scope).PendingSources);
    }
    [Fact] public async Task Background_timeout_is_a_bounded_failure_while_queue_and_ledger_are_retained()
    {
        Configure(UnderstandingMode.Proposals);_sources.Add(2);_owner.Accumulate(_sources,_scope);
        var clock=new TimeoutClock();_models.WaitForCancellation=true;
        var service=new UnderstandingReviewService(_owner,_sources,_sources,_models,_lifetime,clock);
        var first=service.ProcessNextAsync(_scope);await _models.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.Expire();await first;
        var second=service.ProcessNextAsync(_scope);await Task.Yield();clock.Expire();await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(_owner.WorkSummary(_scope).Batches,b=>b.State==ReviewWorkState.Failed);
        Assert.Equal(0,_models.Dispatches);
    }
    [Fact] public async Task Revocation_after_claim_refuses_original_body_reads_before_preparation()
    {
        UnderstandingService? guarded=null;var revoked=false;
        guarded=new(_db,_sources,diagnostics:d=>
        {if(d.Stage=="work_claimed"&&!revoked){revoked=true;guarded!.Configure(_scope,guarded.State(_scope).Revision,new());}});
        guarded.Configure(_scope,0,new(UnderstandingMode.Proposals,ReadSources:true,ExternalProcessing:true,SaveCandidates:true));
        _sources.Add(2);guarded.Accumulate(_sources,_scope);
        var service=new UnderstandingReviewService(guarded,_sources,_sources,_models,_lifetime,TimeProvider.System);
        Assert.True(await service.ProcessNextAsync(_scope));Assert.Equal(0,_sources.BodyReads);Assert.Equal(0,_models.Dispatches);
    }
    internal sealed class Models:IReviewModelFactory
    {
        public bool Allowed=true;public int Dispatches;public Action? BeforeDispatch;public bool WaitForCancellation;
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Available(ConversationScope scope)=>Allowed;
        public IReviewModel Create(ConversationScope scope,UnderstandingState state,IChatDispatchGate admission)=>new Model(this,admission);
        private sealed class Model(Models owner,IChatDispatchGate admission):IReviewModel
        {
            public async Task<ReviewModelResult> ReviewAsync(ReviewModelInput input,CancellationToken token=default)
            {
                await Task.Yield();owner.Entered.TrySetResult();if(owner.WaitForCancellation)await Task.Delay(Timeout.Infinite,token);
                owner.BeforeDispatch?.Invoke();input.EnsureCurrent(token);
                admission.Dispatch(new ChatRequest("mash","c",[new(ChatMessageRole.User,"Synthetic")]),
                    ModelRouteKey.From(new ModelConnectionSettings("deepseek","https://api.deepseek.com/v1","deepseek-flash")),()=>++owner.Dispatches,token);
                var proposal=new UnderstandingProposal("candidate-"+input.OperationId,UnderstandingDomain.Self,UnderstandingKind.CurrentInterest,"assistant",
                    "Synthetic current interest","Synthetic current project at 2026-10-20",input.Originals.Select(m=>m.Source).ToArray());
                return new([proposal],input.Originals.Select(m=>m.Coverage).ToArray(),new ChatUsage(100,10),new(100,TokenCountKind.Estimated,new('B',64)),new('A',64));
            }
        }
    }
    private sealed class TimeoutClock:TimeProvider
    {
        private readonly List<Timer> _timers=[];
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {var timer=new Timer(callback,state);lock(_timers)_timers.Add(timer);return timer;}
        public void Expire(){Timer[] timers;lock(_timers)timers=_timers.ToArray();foreach(var timer in timers)timer.Fire();}
        private sealed class Timer(TimerCallback callback,object? state):ITimer
        {private bool _disposed;public bool Change(TimeSpan dueTime,TimeSpan period)=>!_disposed;
            public void Fire(){if(!_disposed)callback(state);}public void Dispose()=>_disposed=true;public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}}
    }
}
