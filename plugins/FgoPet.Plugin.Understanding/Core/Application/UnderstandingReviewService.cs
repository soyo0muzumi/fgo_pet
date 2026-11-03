using FgoPet.Core.Dialogue;
using FgoPet.Understanding.Contracts;
using System.Text;

namespace FgoPet.Understanding;

public sealed record UnderstandingRunStatus(DateTimeOffset At, string Stage, string? Code = null, int NewItems = 0);

public sealed class UnderstandingReviewService(UnderstandingService owner,IExperienceTimeline timeline,
    IExperienceSourceReader originals,IReviewModelFactory models,IDialogueContextLifetime lifetime,TimeProvider clock,
    Action<UnderstandingDiagnostic>? diagnostics=null,
    Func<ConversationScope,CancellationToken,Task<string?>>? authorshipContext=null,TimeSpan? taskTimeout=null)
{
    private readonly TimeSpan _taskTimeout=taskTimeout is null?TimeSpan.FromMinutes(1):
        taskTimeout.Value>TimeSpan.Zero&&taskTimeout.Value<=TimeSpan.FromMinutes(2)?taskTimeout.Value:
        throw new ArgumentOutOfRangeException(nameof(taskTimeout));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,UnderstandingRunStatus> _status = new();
    public UnderstandingRunStatus? Status(ConversationScope scope) => _status.GetValueOrDefault(scope.Key);
    private void Log(ConversationScope scope,string stage,string? code=null)=>UnderstandingDiagnostics.Write(diagnostics,clock,scope.Key,stage,code);
    private void SetStatus(ConversationScope scope,string stage,string? code=null,int items=0)
    {
        _status[scope.Key]=new(clock.GetUtcNow(),stage,code,items);
        Log(scope,"review_"+stage,code);
    }
    public async Task<bool> ProcessNextAsync(ConversationScope scope,CancellationToken token=default)
    {
        var stage="budget_check";
        try
        {
            token.ThrowIfCancellationRequested();Log(scope,"budget_check_started");
            if(!models.Available(scope)){Log(scope,"budget_check_completed","UNDERSTANDING_MODEL_OR_BUDGET_UNAVAILABLE");SetStatus(scope,"waiting_budget");return false;}
            Log(scope,"budget_check_completed");
            stage="foreground_check";Log(scope,"foreground_check_started");
            using var lease=lifetime.TryAcquireBackground(token);if(lease is null){Log(scope,"foreground_check_completed","UNDERSTANDING_FOREGROUND_BUSY");SetStatus(scope,"waiting_foreground");return false;}
            Log(scope,"foreground_check_completed");
            stage="input_revision";Log(scope,"input_revision_started");
            var revision=timeline.InputRevision(scope);Log(scope,"input_revision_completed");
            stage="batch_claim";Log(scope,"batch_claim_started");
            var batch=owner.ClaimWork(scope,revision);Log(scope,"batch_claim_completed",batch is null?"UNDERSTANDING_NO_CLAIMABLE_BATCH":null);
            if(batch is null)return false;
            stage="review_execution";
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
        catch(Exception error){Log(scope,stage+"_failed",UnderstandingDiagnostics.FailureCode(error));throw;}
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
        // Desktop remains one minute. An explicit host budget is capped at two; provider/foreground limits are unchanged.
        using var timeout=new CancellationTokenSource(_taskTimeout,clock);
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
        // Maintenance is source-authorized; first-order dialogue-use is a separate grant.
        var targets=owner.List(scope,false).Concat(owner.List(scope,true)).DistinctBy(e=>e.Id)
            .Where(e=>e.Life!=UnderstandingLife.Retracted&&e.Life!=UnderstandingLife.Resolved)
            .OrderByDescending(e=>e.LastReviewedAt??e.ObservedAt).Take(8).ToArray();
        var authored=authorshipContext is null?null:await authorshipContext(scope,token).ConfigureAwait(false);
        Current(token);
        var clueHeads=selected.Where(i=>i.StartScalar==0&&i.IncludedScalars==i.Head.TotalScalars).Select(i=>i.Head).ToArray();
        RecurrenceDiscovery? discovery=null;
        // Hosts without context-isolation records retain the existing v0.1 dispatch/permission contract.
        if(originals is IRecurrenceIsolationReader&&material.All(o=>o.Coverage.Complete)&&
            state.Policy.Mode!=UnderstandingMode.Off&&state.Policy.SaveCandidates&&
            models.CreateRecurrence(scope,state,gate) is {} detector)
        {
            var currentSources=material.Select(o=>o.Source).ToArray();
            var isolated=originals as IRecurrenceIsolationReader;
            var available=isolated is not null&&currentSources.All(isolated.IsIsolated)
                ?owner.RecurrenceClues(scope).Where(c=>c.SourceRefs.All(h=>isolated.IsIsolated(h.Source)&&
                    currentSources.All(s=>s.Scope.ConversationId!=h.Source.Scope.ConversationId&&s.OccurredAtUtc>h.Source.OccurredAtUtc))).ToArray():[];
            Log(scope,"recurrence_discovery_started");
            discovery=await detector.DiscoverAsync(new(operationId,scope,material.ToArray(),[],Current,authored),available,token).ConfigureAwait(false);
            Current(token);
            Log(scope,"recurrence_discovery_completed");
            // Stage A survives every later retrieval/review/classification failure. It grants no Self effect.
            lease.Commit(()=>owner.CommitRecurrenceClues(ticket,discovery.Clues,material.ToArray(),clueHeads,
                ()=>timeline.InputRevision(scope)==revision,token));
            Current(token);
            Log(scope,"recurrence_retrieval_started");
            var selection=await detector.RetrieveAsync(new(operationId,scope,material.ToArray(),[],Current,authored),available,discovery,token).ConfigureAwait(false);
            Current(token);
            if(selection.BroadIds.Count>8||selection.BroadIds.Distinct().Count()!=selection.BroadIds.Count||
                selection.BroadIds.Any(id=>!available.Any(c=>c.Id==id))||selection.RetrievedIds.Count>3||
                selection.RetrievedIds.Distinct().Count()!=selection.RetrievedIds.Count||
                selection.RetrievedIds.Any(id=>!selection.BroadIds.Contains(id)))throw new InvalidOperationException("UNDERSTANDING_RECURRENCE_INVALID_SELECTION");
            Log(scope,"recurrence_retrieval_completed");
            foreach(var id in selection.RetrievedIds)
            {
                var clue=available.Single(c=>c.Id==id);
                var historical=clue.SourceRefs.Where(h=>!selected.Any(i=>i.Head.Source==h.Source)).ToArray();
                if(selected.Count+historical.Length>8||selected.Sum(i=>i.IncludedScalars)+historical.Sum(h=>h.TotalScalars)>6000)
                {Log(scope,"recurrence_source_skipped","UNDERSTANDING_RECURRENCE_MATERIAL_LIMIT");continue;}
                selected.AddRange(historical.Select(h=>new ReviewWorkItem("historical",h,0,h.TotalScalars)));
                roots=selected.Select(i=>i.Head.Source).ToArray();
                coverage=selected.Select(i=>new UnderstandingSourceCoverage(i.Head.Source,i.StartScalar,i.IncludedScalars,i.Head.TotalScalars)).ToArray();
                ticket=owner.Begin(scope,operationId,roots,manual,token,coverage);
                foreach(var head in historical)
                {
                    Log(scope,"recurrence_original_read_started");Read(head,0,head.TotalScalars);
                    var raw=material[^1];
                    if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw.Text)))!=head.Source.Fingerprint)
                        throw new InvalidOperationException("UNDERSTANDING_SOURCE_CHANGED");
                    Log(scope,"recurrence_original_read_completed");
                }
            }
        }
        var input=new ReviewModelInput(operationId,scope,material,targets,Current,authored);
        Current(token);var model=models.Create(scope,state,gate);
        var result=await model.ReviewAsync(input,token).ConfigureAwait(false);Current(token);
        if(!result.SuppliedCoverage.SequenceEqual(coverage))throw new InvalidOperationException("UNDERSTANDING_COVERAGE_CHANGED");
        Log(scope,"evidence_validation_started");
        var classification=state.Policy.Mode==UnderstandingMode.Bounded||result.Proposals.Any(p=>p.Origin is not null)
            ?await models.ClassifyAsync(input,result,state,gate,token).ConfigureAwait(false):null;
        Log(scope,"evidence_validation_completed");
        Current(token);
        return lease.Commit(()=>owner.Apply(ticket,result.Proposals,token,classification,()=>timeline.InputRevision(scope)==revision,material));

        void Read(ExperienceSourceHead head,int start,int count)
        {
            token.ThrowIfCancellationRequested();lease.CheckCurrent();
            if(!timeline.IsCurrent(head.Source))throw new InvalidOperationException("UNDERSTANDING_SOURCE_CHANGED");
            var read=lease.Commit(()=>owner.AdmitReview(ticket,timeline,revision,()=>originals.Read(scope,head.Anchor,token),token));
            if(read.Status!=ExperienceSourceStatus.Available||read.Source is not {} source||source.Scope!=scope||source.Anchor!=head.Anchor||
                source.SourceVersion!=head.Source.Fingerprint||source.Kind!=head.Source.Kind)
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
