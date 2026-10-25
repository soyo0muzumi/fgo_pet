using FgoPet.Core.Dialogue;
using FgoPet.Understanding.Contracts;
using System.Text;

namespace FgoPet.Understanding;

public sealed record UnderstandingRunStatus(DateTimeOffset At, string Stage, string? Code = null, int NewItems = 0);

public sealed class UnderstandingReviewService(UnderstandingService owner,IExperienceTimeline timeline,
    IExperienceSourceReader originals,IReviewModelFactory models,IDialogueContextLifetime lifetime,TimeProvider clock)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,UnderstandingRunStatus> _status = new();
    public UnderstandingRunStatus? Status(ConversationScope scope) => _status.GetValueOrDefault(scope.Key);
    private void SetStatus(ConversationScope scope,string stage,string? code=null,int items=0) => _status[scope.Key]=new(clock.GetUtcNow(),stage,code,items);
    public async Task<bool> ProcessNextAsync(ConversationScope scope,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();if(!models.Available(scope)){SetStatus(scope,"waiting_budget");return false;}
        using var lease=lifetime.TryAcquireBackground(token);if(lease is null){SetStatus(scope,"waiting_foreground");return false;}
        var batch=owner.ClaimWork(scope,timeline.InputRevision(scope));if(batch is null)return false;
        SetStatus(scope,"running");
        try
        {
            // Receipt recovery wins even when a process died after commit and before queue acknowledgement.
            var receipt=owner.Receipt(scope,batch.Id)??await RunAsync(batch.Id,scope,batch.Items,false,lease,lease.Token).ConfigureAwait(false);
            if(receipt.Items.Any(i=>i.Code=="UNDERSTANDING_STALE_INPUT"))owner.PauseWork(batch,"UNDERSTANDING_STALE_INPUT",newInput:true);
            else owner.FinishWork(batch,receipt);
            var code=receipt.Items.FirstOrDefault(i=>i.Code is not null)?.Code;
            SetStatus(scope,code is null?"completed":"failed",code,receipt.Items.Count(i=>i.Outcome is UnderstandingOutcome.Applied or UnderstandingOutcome.ProposedPending));return true;
        }
        catch(OperationCanceledException)
        {owner.PauseWork(batch,"UNDERSTANDING_CANCELLED");SetStatus(scope,"interrupted","UNDERSTANDING_CANCELLED");token.ThrowIfCancellationRequested();return true;}
        catch(InvalidOperationException error)
        {
            if(error.Message=="UNDERSTANDING_STALE_INPUT")owner.PauseWork(batch,error.Message,newInput:true);
            else if(error.Message is "UNDERSTANDING_SOURCE_UNAVAILABLE" or "UNDERSTANDING_BUDGET_LIMITED" or "UNDERSTANDING_BUDGET_UNAVAILABLE")owner.PauseWork(batch,error.Message);
            else owner.DeferWork(batch,"UNDERSTANDING_REVIEW_REFUSED",transient:false);
            SetStatus(scope,"failed",error.Message is "UNDERSTANDING_STALE_INPUT" or "UNDERSTANDING_SOURCE_UNAVAILABLE" or "UNDERSTANDING_BUDGET_LIMITED" or "UNDERSTANDING_BUDGET_UNAVAILABLE" ? error.Message : "UNDERSTANDING_REVIEW_REFUSED");return true;
        }
        catch(TimeoutException)
        {owner.DeferWork(batch,"UNDERSTANDING_REVIEW_TIMEOUT",transient:true);SetStatus(scope,"failed","UNDERSTANDING_REVIEW_TIMEOUT");return true;}
        catch(Exception)
        {owner.DeferWork(batch,"UNDERSTANDING_REVIEW_FAILED",transient:true);SetStatus(scope,"failed","UNDERSTANDING_REVIEW_FAILED");return true;}
    }
    public async Task<UnderstandingReceipt> ManualAsync(ConversationScope scope,string operationId,IReadOnlyList<ExperienceSourceHead> selection,
        CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();if(owner.Receipt(scope,operationId) is {} replay)return replay;
        if(!models.Available(scope))throw new InvalidOperationException("UNDERSTANDING_BUDGET_UNAVAILABLE");
        if(selection is null||selection.Count is <1 or >8||selection.Sum(s=>(long)s.TotalScalars)>6000)
            throw new ArgumentException("UNDERSTANDING_INVALID_SELECTION");
        using var lease=lifetime.TryAcquireBackground(token)??throw new InvalidOperationException("UNDERSTANDING_FOREGROUND_BUSY");
        var items=selection.Select((h,i)=>new ReviewWorkItem("manual-"+i,h,0,h.TotalScalars)).ToArray();
        // One explicit operation cannot be replayed after an unknown dispatch. Only a new manual ID can retry.
        if(!owner.TryReserveAttempt(scope,operationId,selection.Select(h=>h.Source).ToArray(),manual:true))
            throw new InvalidOperationException("UNDERSTANDING_ATTEMPT_NOT_ADMITTED");
        try
        {
            var receipt=await RunAsync(operationId,scope,items,true,lease,lease.Token).ConfigureAwait(false);
            if(!receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.ReviewedNotPersisted))
                owner.CompleteAttempt(scope,operationId,receipt.Items.First().Outcome);
            return receipt;
        }
        catch{owner.CompleteAttempt(scope,operationId,UnderstandingOutcome.Failed);throw;}
    }
    public void ConfirmManualPreviewDisplayed(ConversationScope scope,string operationId)
    {
        var receipt=owner.Receipt(scope,operationId)??throw new InvalidOperationException("UNDERSTANDING_PREVIEW_UNKNOWN");
        if(!receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.ReviewedNotPersisted))
            throw new InvalidOperationException("UNDERSTANDING_PREVIEW_UNKNOWN");
        owner.CompleteAttempt(scope,operationId,UnderstandingOutcome.ReviewedNotPersisted);
    }
    private async Task<UnderstandingReceipt> RunAsync(string operationId,ConversationScope scope,IReadOnlyList<ReviewWorkItem> items,
        bool manual,IDialogueBackgroundLease lease,CancellationToken token)
    {
        // New background work has its own one-minute task bound; existing provider/foreground limits are unchanged.
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(1),clock);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        token=cancellation.Token;
        try
        {
        lease.CheckCurrent();
        var state=owner.State(scope);var revision=timeline.InputRevision(scope);
        var material=new List<ReviewOriginal>();var selected=items.ToList();
        // A resumed old range reads available new originals as additional, explicitly partial context.
        // Their own work records remain pending until their own coverage is committed.
        var remaining=6000-selected.Sum(i=>i.IncludedScalars);
        if(!manual&&selected.Count<8&&remaining>0)
            foreach(var head in owner.IncrementalHeads(scope,items.Max(i=>i.Head.Source.SourceRevision),8-selected.Count))
            {var take=Math.Min(remaining,Math.Min(400,head.TotalScalars));if(take==0)break;selected.Add(new("context",head,0,take));remaining-=take;}
        var roots=selected.Select(i=>i.Head.Source).ToArray();
        var coverage=selected.Select(i=>new UnderstandingSourceCoverage(i.Head.Source,i.StartScalar,i.IncludedScalars,i.Head.TotalScalars)).ToArray();
        var ticket=owner.Begin(scope,operationId,roots,manual,token,coverage);
        if(ticket.Generation!=state.Generation||ticket.InputRevision!=state.Revision)throw new InvalidOperationException("UNDERSTANDING_STALE_INPUT");
        void Current(CancellationToken cancellationToken)
        {cancellationToken.ThrowIfCancellationRequested();lease.CheckCurrent();owner.CheckReviewCurrent(ticket,timeline,revision,cancellationToken);}
        var gate=new AdmissionGate((start,cancellationToken)=>lease.Commit(()=>owner.AdmitReview(ticket,timeline,revision,start,cancellationToken)));
        Current(token);
        foreach(var item in selected)Read(item.Head,item.StartScalar,item.IncludedScalars);
        var targets=state.Policy.UseInDialogue
            ?owner.List(scope,false).Where(e=>e.Life!=UnderstandingLife.Retracted&&e.Life!=UnderstandingLife.Resolved).Take(8).ToArray():[];
        var input=new ReviewModelInput(operationId,scope,material,targets,Current);
        Current(token);var model=models.Create(scope,state,gate);
        var result=await model.ReviewAsync(input,token).ConfigureAwait(false);Current(token);
        if(!result.SuppliedCoverage.SequenceEqual(coverage))throw new InvalidOperationException("UNDERSTANDING_COVERAGE_CHANGED");
        var classification=state.Policy.Mode==UnderstandingMode.Bounded
            ?await models.ClassifyAsync(input,result,state,gate,token).ConfigureAwait(false):null;
        Current(token);
        return lease.Commit(()=>owner.Apply(ticket,result.Proposals,token,classification,()=>timeline.InputRevision(scope)==revision));

        void Read(ExperienceSourceHead head,int start,int count)
        {
            token.ThrowIfCancellationRequested();lease.CheckCurrent();
            if(!timeline.IsCurrent(head.Source))throw new InvalidOperationException("UNDERSTANDING_SOURCE_CHANGED");
            var read=lease.Commit(()=>owner.AdmitReview(ticket,timeline,revision,()=>originals.Read(scope,head.Anchor,token),token));
            if(read.Status!=ExperienceSourceStatus.Available||read.Source is not {} source||source.SourceVersion!=head.Source.Fingerprint||source.Kind!=head.Source.Kind)
                throw new InvalidOperationException("UNDERSTANDING_SOURCE_CHANGED");
            if(head.Source.Kind==FgoPet.Extensibility.ConversationSourceKind.External&&
                (source.SourceLabel!=head.Source.SourceLabel||source.Author!=head.Source.Author||
                 string.IsNullOrWhiteSpace(source.SourceLabel)||string.IsNullOrWhiteSpace(source.Author)))
                throw new InvalidOperationException("UNDERSTANDING_SOURCE_CHANGED");
            var scalars=source.Text.EnumerateRunes().ToArray();
            if(scalars.Length!=head.TotalScalars||start<0||count<1||(long)start+count>scalars.Length)
                throw new InvalidOperationException("UNDERSTANDING_COVERAGE_CHANGED");
            material.Add(new(head.Source,string.Concat(scalars.Skip(start).Take(count).Select(r=>r.ToString())),new(head.Source,start,count,scalars.Length)));
        }
        }
        catch(OperationCanceledException)when(timeout.IsCancellationRequested&&!lease.Token.IsCancellationRequested)
        {throw new TimeoutException("UNDERSTANDING_REVIEW_TIMEOUT");}
    }
    private sealed class AdmissionGate(Func<Func<object?>,CancellationToken,object?> admit):IChatDispatchGate
    {
        public T Dispatch<T>(ChatRequest request,ModelRouteKey route,Func<T> start,CancellationToken token)=>(T)admit(()=>start(),token)!;
    }
}
