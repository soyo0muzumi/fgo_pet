using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed class UnderstandingReviewScheduler(UnderstandingService owner,IExperienceTimeline timeline,
    UnderstandingReviewService reviews,TimeProvider clock,Action<UnderstandingDiagnostic>? diagnostics=null):IPostTurnObserver,IAsyncDisposable,IDisposable
{
    private readonly SemaphoreSlim _serial=new(1,1);
    private readonly object _activityGate=new();
    private readonly Dictionary<string,long> _activity=[];
    private readonly Dictionary<string,long> _oldest=[];
    private readonly Dictionary<string,long> _attempted=[];
    private readonly long _started=clock.GetTimestamp();
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private readonly object _lifecycleGate=new();
    private readonly CancellationTokenSource _pulseStopping=new();
    private Task? _stopTask;
    private void Log(string correlation,string stage,string? code=null)=>UnderstandingDiagnostics.Write(diagnostics,clock,correlation,stage,code);
    public void Observe(ConversationCompletedTurn turn)
    {
        // Called by the message owner: only a bounded in-memory wake marker, never SQLite or a model.
        lock(_activityGate)_activity[new ConversationScope(turn.Scope.RoleId,turn.Scope.ProjectId).Key]=clock.GetTimestamp();
    }
    public async Task PulseAsync(CancellationToken token=default)
    {
        CancellationToken stopping;
        lock(_lifecycleGate)
        {
            if(_stopTask is not null){Log("worker","pulse_skipped","UNDERSTANDING_WORKER_STOPPED");return;}
            stopping=_pulseStopping.Token;
        }
        using var pulseCancellation=CancellationTokenSource.CreateLinkedTokenSource(token,stopping);
        token=pulseCancellation.Token;
        if(!await _serial.WaitAsync(0,token).ConfigureAwait(false)){Log("worker","pulse_skipped","UNDERSTANDING_PULSE_BUSY");return;}
        var correlation="worker";var stage="scope_scan";
        try
        {
            Log(correlation,"pulse_started");
            Log(correlation,"scope_scan_started");
            var scopes=owner.AutomaticScopes();
            Log(correlation,"scope_scan_completed",scopes.Count==0?"UNDERSTANDING_NO_AUTOMATIC_SCOPE":null);
            foreach(var scope in scopes)
            {
                correlation=scope.Key;stage="scope_check";
                token.ThrowIfCancellationRequested();
                var cooldown=reviews.Status(scope)?.Stage=="failed"?TimeSpan.FromMinutes(5):TimeSpan.FromMinutes(2);
                if(_attempted.TryGetValue(scope.Key,out var previous)&&clock.GetElapsedTime(previous,clock.GetTimestamp())<cooldown)
                {Log(correlation,"scope_skipped",reviews.Status(scope)?.Stage=="failed"?"UNDERSTANDING_FAILURE_COOLDOWN":"UNDERSTANDING_ATTEMPT_COOLDOWN");continue;}
                stage="source_scan";Log(correlation,"source_scan_started");
                for(var scan=0;scan<8&&owner.Accumulate(timeline,scope,token:token);scan++) { }
                Log(correlation,"source_scan_completed");
                stage="work_summary";Log(correlation,"work_summary_started");
                var work=owner.WorkSummary(scope);
                Log(correlation,"work_summary_completed",work.PendingSources==0?"UNDERSTANDING_QUEUE_EMPTY":"UNDERSTANDING_QUEUE_PENDING");
                if(work.PendingSources==0){_oldest.Remove(scope.Key);continue;}
                var now=clock.GetTimestamp();if(!_oldest.TryGetValue(scope.Key,out var oldest))_oldest[scope.Key]=oldest=now;
                long activity;lock(_activityGate)activity=_activity.GetValueOrDefault(scope.Key,_started);
                var idle=clock.GetElapsedTime(activity,now);var age=clock.GetElapsedTime(oldest,now);
                if(idle<TimeSpan.FromSeconds(120)&&age<TimeSpan.FromMinutes(15)&&work.CompletedTurns<6&&(long)work.PendingScalars*3<6000)
                {Log(correlation,"scope_skipped","UNDERSTANDING_WAITING_THRESHOLD");continue;}
                Log(correlation,"dispatch_eligible",idle>=TimeSpan.FromSeconds(120)?"UNDERSTANDING_IDLE_READY":
                    age>=TimeSpan.FromMinutes(15)?"UNDERSTANDING_AGE_READY":work.CompletedTurns>=6?"UNDERSTANDING_TURNS_READY":"UNDERSTANDING_SIZE_READY");
                _attempted[scope.Key]=clock.GetTimestamp();
                stage="review_dispatch";Log(correlation,"review_dispatch_started");
                var processed=await reviews.ProcessNextAsync(scope,token).ConfigureAwait(false);
                Log(correlation,"review_dispatch_completed",processed?null:"UNDERSTANDING_DISPATCH_DEFERRED");
            }
            Log("worker","pulse_completed");
        }
        catch(Exception error){Log(correlation,stage+"_failed",UnderstandingDiagnostics.FailureCode(error));throw;}
        finally{_serial.Release();}
    }
    public void Start(CancellationToken processStopping)
    {
        if(_stopTask is not null)throw new InvalidOperationException("UNDERSTANDING_WORKER_STOPPED");
        if(_loop is not null)throw new InvalidOperationException("UNDERSTANDING_WORKER_ALREADY_STARTED");
        Log("worker","worker_starting");
        try{owner.RecoverWork();Log("worker","work_recovery_completed");}
        catch(Exception error){Log("worker","work_recovery_failed",UnderstandingDiagnostics.FailureCode(error));throw;}
        _stopping=CancellationTokenSource.CreateLinkedTokenSource(processStopping);
        _loop=Task.Run(()=>RunAsync(_stopping.Token));
    }
    private async Task RunAsync(CancellationToken token)
    {
        Log("worker","worker_started");
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5),clock);
        try
        {
            do
            {
                try{await PulseAsync(token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
                catch(Exception)
                {Log("worker","review_worker_failed","UNDERSTANDING_WORKER_FAILED");}
            }while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested) { }
        finally{Log("worker","worker_stopped",token.IsCancellationRequested?"UNDERSTANDING_CANCELLED":null);}
    }
    public void Dispose()=>_=StopAsync();
    public ValueTask DisposeAsync()=>new(StopAsync());
    private Task StopAsync()
    {
        lock(_lifecycleGate)return _stopTask??=StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        Log("worker","worker_stopping");
        await _pulseStopping.CancelAsync().ConfigureAwait(false);
        if(_stopping is not null)await _stopping.CancelAsync().ConfigureAwait(false);
        if(_loop is not null)await _loop.ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
        _stopping?.Dispose();_stopping=null;_pulseStopping.Dispose();
    }
}
