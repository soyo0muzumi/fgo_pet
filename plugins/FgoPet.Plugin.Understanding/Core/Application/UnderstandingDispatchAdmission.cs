using FgoPet.Core.Dialogue;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    public UnderstandingReceipt? Receipt(ConversationScope scope,string operationId)
    {lock(_gate){EnsureSchema();using var db=database.Open();return ReadReceipt(db,null,scope,operationId);}}

    public void CheckReviewCurrent(UnderstandingTicket ticket,IExperienceTimeline timeline,long inputRevision,CancellationToken token=default)
        =>AdmitReview(ticket,timeline,inputRevision,()=>true,token);

    // The short write reservation blocks other runtime-store writers through the synchronous HTTP handoff.
    // It is released before awaiting the network; accounting uses its separate durable database.
    public T AdmitReview<T>(UnderstandingTicket ticket,IExperienceTimeline timeline,long inputRevision,Func<T> start,CancellationToken token=default)
    {
        lock(_gate)
        {
            token.ThrowIfCancellationRequested();EnsureSchema();using var db=database.Open();var version=DataVersion(db);
            var denial=CheckSources(ticket.Scope,ticket.Sources,ticket.Id);
            var originalRevision=timeline.InputRevision(ticket.Scope);
            using var tx=db.BeginTransaction();var state=ReadState(db,tx,ticket.Scope);
            if(denial is not null)throw new InvalidOperationException(denial);
            if(!state.Policy.Enabled||!state.Policy.ReadSources||!state.Policy.ExternalProcessing||
                !ticket.Manual&&(!Automatic(state)||ticket.Sources.All(s=>s.SourceRevision<=state.AutoSince)))
                throw new InvalidOperationException("UNDERSTANDING_NOT_AUTHORIZED");
            if(state.Generation!=ticket.Generation||state.Revision!=ticket.InputRevision||version!=DataVersion(db,tx)||originalRevision!=inputRevision)
                throw new InvalidOperationException("UNDERSTANDING_STALE_INPUT");
            token.ThrowIfCancellationRequested();var result=start();tx.Commit();return result;
        }
    }
}
