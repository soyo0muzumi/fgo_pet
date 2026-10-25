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
    private readonly long _started=clock.GetTimestamp();
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private readonly object _lifecycleGate=new();
    private readonly CancellationTokenSource _pulseStopping=new();
    private Task? _stopTask;
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
            if(_stopTask is not null)return;
            stopping=_pulseStopping.Token;
        }
        using var pulseCancellation=CancellationTokenSource.CreateLinkedTokenSource(token,stopping);
        token=pulseCancellation.Token;
        if(!await _serial.WaitAsync(0,token).ConfigureAwait(false))return;
        try
        {
            foreach(var scope in owner.AutomaticScopes())
            {
                token.ThrowIfCancellationRequested();
                for(var scan=0;scan<8&&owner.Accumulate(timeline,scope,token:token);scan++) { }
                var work=owner.WorkSummary(scope);
                if(work.PendingSources==0){_oldest.Remove(scope.Key);continue;}
                var now=clock.GetTimestamp();if(!_oldest.TryGetValue(scope.Key,out var oldest))_oldest[scope.Key]=oldest=now;
                long activity;lock(_activityGate)activity=_activity.GetValueOrDefault(scope.Key,_started);
                var idle=clock.GetElapsedTime(activity,now);var age=clock.GetElapsedTime(oldest,now);
                if(idle<TimeSpan.FromSeconds(120)&&age<TimeSpan.FromMinutes(15)&&work.CompletedTurns<6&&(long)work.PendingScalars*3<6000)continue;
                await reviews.ProcessNextAsync(scope,token).ConfigureAwait(false);
            }
        }
        finally{_serial.Release();}
    }
    public void Start(CancellationToken processStopping)
    {
        if(_stopTask is not null)throw new InvalidOperationException("UNDERSTANDING_WORKER_STOPPED");
        if(_loop is not null)throw new InvalidOperationException("UNDERSTANDING_WORKER_ALREADY_STARTED");
        owner.RecoverWork();_stopping=CancellationTokenSource.CreateLinkedTokenSource(processStopping);
        _loop=Task.Run(()=>RunAsync(_stopping.Token));
    }
    private async Task RunAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5),clock);
        try
        {
            do
            {
                try{await PulseAsync(token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
                catch(Exception)
                {try{diagnostics?.Invoke(new(clock.GetUtcNow(),"worker","review_worker_failed","UNDERSTANDING_WORKER_FAILED"));}catch(Exception){}}
            }while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested) { }
    }
    public void Dispose()=>_=StopAsync();
    public ValueTask DisposeAsync()=>new(StopAsync());
    private Task StopAsync()
    {
        lock(_lifecycleGate)return _stopTask??=StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        await _pulseStopping.CancelAsync().ConfigureAwait(false);
        if(_stopping is not null)await _stopping.CancelAsync().ConfigureAwait(false);
        if(_loop is not null)await _loop.ConfigureAwait(false);
        await _serial.WaitAsync().ConfigureAwait(false);
        _serial.Release();
        _stopping?.Dispose();_stopping=null;_pulseStopping.Dispose();
    }
}
