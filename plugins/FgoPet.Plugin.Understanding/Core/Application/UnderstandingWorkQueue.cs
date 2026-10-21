using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;
using System.Text.Json;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    private static bool Automatic(UnderstandingState state)=>state.Policy.Enabled&&state.Policy.ReadSources&&state.Policy.SaveCandidates&&state.Policy.Mode!=UnderstandingMode.Off;
    public IReadOnlyList<ConversationScope> AutomaticScopes()
    {
        lock(_gate){EnsureSchema();using var db=database.Open();return Scopes(db,null).Where(s=>Automatic(ReadState(db,null,s))).ToArray();}
    }
    public bool Accumulate(IExperienceTimeline timeline,ConversationScope scope,int capacity=256,CancellationToken token=default)
    {
        if(capacity is <1 or >256)throw new ArgumentOutOfRangeException(nameof(capacity));
        lock(_gate)
        {
            token.ThrowIfCancellationRequested();EnsureSchema();using var db=database.Open();var state=ReadState(db,null,scope);
            if(!Automatic(state))return false;
            Execute(db,null,"UPDATE understanding_work_items SET state=7,batch_id=NULL WHERE scope=$scope AND state IN (0,1) AND json_extract(head_json,'$.Source.SourceRevision')<=$since",
                ("$scope",scope.Key),("$since",state.AutoSince));
            using var count=Command(db,null,"SELECT COUNT(*) FROM understanding_work_items WHERE scope=$scope AND state IN (0,1)",("$scope",scope.Key));
            var free=capacity-checked((int)(long)count.ExecuteScalar()!);if(free<=0)return false;
            using var previous=Command(db,null,"SELECT cursor_json FROM understanding_work_cursors WHERE scope=$scope",("$scope",scope.Key));
            var cursor=previous.ExecuteScalar() is string json?JsonSerializer.Deserialize<ExperienceScanCursor>(json)!:new(state.AutoSince,"\uffff");
            if(cursor.Sequence<state.AutoSince)cursor=new(state.AutoSince,"\uffff");
            var version=DataVersion(db);
            var page=timeline.ScanHeads(scope,cursor,Math.Min(32,free),token);
            if(page.Status!=ExperienceSourceStatus.Available)throw new InvalidOperationException("UNDERSTANDING_SOURCE_UNAVAILABLE");
            using var tx=db.BeginTransaction();var current=ReadState(db,tx,scope);
            if(version!=DataVersion(db,tx)||current.Generation!=state.Generation||current.Revision!=state.Revision)return false;
            foreach(var head in page.Items)
            {
                if(head.Source.SourceRevision<=state.AutoSince||head.Source.Scope.RoleId!=scope.ServantId||head.Source.Scope.ProjectId!=scope.ProjectId||head.TotalScalars is <1 or >12000)
                    throw new InvalidOperationException("UNDERSTANDING_SCAN_INVALID");
                Execute(db,tx,"INSERT OR IGNORE INTO understanding_work_items VALUES($scope,$id,$head,0,0,NULL)",
                    ("$scope",scope.Key),("$id",HashId(JsonSerializer.Serialize(head.Source))),("$head",JsonSerializer.Serialize(head)));
            }
            Execute(db,tx,"INSERT INTO understanding_work_cursors VALUES($scope,$cursor) ON CONFLICT(scope) DO UPDATE SET cursor_json=$cursor",
                ("$scope",scope.Key),("$cursor",JsonSerializer.Serialize(page.Cursor)));
            token.ThrowIfCancellationRequested();tx.Commit();Log(scope.Key,"work_range_registered");return page.Cursor!=cursor;
        }
    }
    public ReviewWorkSummary WorkSummary(ConversationScope scope)
    {
        lock(_gate)
        {
            EnsureSchema();using var db=database.Open();var state=ReadState(db,null,scope);
            using var work=Command(db,null,"SELECT head_json,scalar_offset FROM understanding_work_items WHERE scope=$scope AND state IN (0,1)",("$scope",scope.Key));
            var count=0;var scalars=0;var turns=0;
            using(var r=work.ExecuteReader())while(r.Read())
            {var h=JsonSerializer.Deserialize<ExperienceSourceHead>(r.GetString(0))!;if(h.Source.SourceRevision<=state.AutoSince)continue;
                count++;scalars+=h.TotalScalars-r.GetInt32(1);if(h.Source.Kind==ConversationSourceKind.Assistant)turns++;}
            using var batches=Command(db,null,"SELECT id,state FROM understanding_work_batches WHERE scope=$scope ORDER BY rowid",("$scope",scope.Key));
            using var reader=batches.ExecuteReader();var result=new List<(string,ReviewWorkState)>();
            while(reader.Read())result.Add((reader.GetString(0),(ReviewWorkState)reader.GetInt32(1)));
            return new(count,scalars,turns,result);
        }
    }
    public ReviewWorkBatch? ClaimWork(ConversationScope scope,long inputRevision=0)
    {
        lock(_gate)
        {
            EnsureSchema();using var db=database.Open();using var tx=db.BeginTransaction();var state=ReadState(db,tx,scope);
            if(!Automatic(state)||!state.Policy.ExternalProcessing)return null;
            using(var running=Command(db,tx,"SELECT COUNT(*) FROM understanding_work_batches WHERE scope=$scope AND state=1",("$scope",scope.Key)))
                if((long)running.ExecuteScalar()!>0)return null;
            Execute(db,tx,"UPDATE understanding_work_items SET state=6 WHERE scope=$scope AND batch_id IN (SELECT b.id FROM understanding_work_batches b WHERE b.scope=$scope AND b.state=0 AND json_extract(b.batch_json,'$.Attempts')>=2 AND NOT EXISTS(SELECT 1 FROM understanding_receipts r WHERE r.scope=b.scope AND r.operation_id=b.id))",("$scope",scope.Key));
            Execute(db,tx,"UPDATE understanding_work_batches SET state=6,code='UNDERSTANDING_RETRY_LIMIT' WHERE scope=$scope AND state=0 AND json_extract(batch_json,'$.Attempts')>=2 AND NOT EXISTS(SELECT 1 FROM understanding_receipts r WHERE r.scope=understanding_work_batches.scope AND r.operation_id=understanding_work_batches.id)",("$scope",scope.Key));
            ReviewWorkBatch? batch=null;
            using(var queued=Command(db,tx,"SELECT batch_json FROM understanding_work_batches WHERE scope=$scope AND state=0 ORDER BY rowid",("$scope",scope.Key)))
            using(var r=queued.ExecuteReader())while(r.Read())
            {
                var candidate=JsonSerializer.Deserialize<ReviewWorkBatch>(r.GetString(0))!;
                if(ReadReceipt(db,tx,scope,candidate.Id) is not null||candidate.Generation==state.Generation&&candidate.InputRevision==inputRevision&&candidate.Items.All(i=>i.Head.Source.SourceRevision>state.AutoSince)&&candidate.Attempts<2){batch=candidate;break;}
            }
            if(batch is null)
            {
                // A policy generation change never grants an old batch permission. Rebind only still-eligible unprocessed originals.
                Execute(db,tx,"UPDATE understanding_work_items SET state=0,batch_id=NULL WHERE scope=$scope AND state=1 AND batch_id IN (SELECT id FROM understanding_work_batches WHERE scope=$scope AND state=0)",("$scope",scope.Key));
                Execute(db,tx,"UPDATE understanding_work_batches SET state=7 WHERE scope=$scope AND state=0",("$scope",scope.Key));
                using var pending=Command(db,tx,"SELECT id,head_json,scalar_offset FROM understanding_work_items WHERE scope=$scope AND state=0 ORDER BY rowid LIMIT 32",("$scope",scope.Key));
                var items=new List<ReviewWorkItem>();var remaining=2000;
                using(var r=pending.ExecuteReader())while(r.Read())
                {
                    var head=JsonSerializer.Deserialize<ExperienceSourceHead>(r.GetString(1))!;
                    if(head.Source.SourceRevision<=state.AutoSince)continue;
                    var start=r.GetInt32(2);var take=Math.Min(remaining,head.TotalScalars-start);
                    items.Add(new(r.GetString(0),head,start,take));remaining-=take;if(items.Count==8||remaining==0)break;
                }
                if(items.Count==0){tx.Commit();return null;}
                var id="work-"+HashId(JsonSerializer.Serialize(new{scope=scope.Key,state.Generation,inputRevision,items}))[..48];
                batch=new(id,scope,state.Generation,false,items,InputRevision:inputRevision);
            }
            batch=batch with{Attempts=batch.Attempts+1};
            Execute(db,tx,"INSERT INTO understanding_work_batches VALUES($scope,$id,$json,1,NULL,NULL) ON CONFLICT(scope,id) DO UPDATE SET batch_json=$json,state=1",
                ("$scope",scope.Key),("$id",batch.Id),("$json",JsonSerializer.Serialize(batch)));
            foreach(var item in batch.Items)Execute(db,tx,"UPDATE understanding_work_items SET state=1,batch_id=$batch WHERE scope=$scope AND id=$id",
                ("$scope",scope.Key),("$id",item.Id),("$batch",batch.Id));
            tx.Commit();Log(batch.Id,"work_claimed");return batch;
        }
    }
    public void FinishWork(ReviewWorkBatch batch,UnderstandingReceipt receipt)
    {
        if(receipt.OperationId!=batch.Id||receipt.Items.Count==0)throw new ArgumentException("UNDERSTANDING_WORK_RECEIPT_INVALID");
        lock(_gate)
        {
            EnsureSchema();using var db=database.Open();using var tx=db.BeginTransaction();
            using var previous=Command(db,tx,"SELECT state FROM understanding_work_batches WHERE scope=$scope AND id=$id",("$scope",batch.Scope.Key),("$id",batch.Id));
            if(previous.ExecuteScalar() is not long old||old!=(int)ReviewWorkState.Running)return;
            var state=receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.Failed)?ReviewWorkState.Failed:
                receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.ProposedPending)?ReviewWorkState.ProposedPending:
                receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.ReviewedNotPersisted)?ReviewWorkState.ReviewedNotPersisted:
                receipt.Items.Any(i=>i.Outcome==UnderstandingOutcome.Applied)?ReviewWorkState.Applied:ReviewWorkState.NoChange;
            Execute(db,tx,"UPDATE understanding_work_batches SET state=$state,outcomes_json=$outcomes WHERE scope=$scope AND id=$id",
                ("$scope",batch.Scope.Key),("$id",batch.Id),("$state",(int)state),("$outcomes",JsonSerializer.Serialize(receipt.Items)));
            foreach(var item in batch.Items)
            {
                var next=item.StartScalar+item.IncludedScalars;
                var itemState=state==ReviewWorkState.Failed?ReviewWorkState.Failed:next<item.Head.TotalScalars?ReviewWorkState.Unprocessed:state;
                Execute(db,tx,"UPDATE understanding_work_items SET scalar_offset=$next,state=$state,batch_id=NULL WHERE scope=$scope AND id=$id AND batch_id=$batch",
                    ("$scope",batch.Scope.Key),("$id",item.Id),("$next",next),("$state",(int)itemState),("$batch",batch.Id));
            }
            tx.Commit();Log(batch.Id,"work_completed");
        }
    }
    public void DeferWork(ReviewWorkBatch batch,string code,bool transient)
    {
        lock(_gate)
        {
            EnsureSchema();using var db=database.Open();using var tx=db.BeginTransaction();
            var retry=transient&&batch.Attempts<2;
            Execute(db,tx,"UPDATE understanding_work_batches SET state=$state,code=$code WHERE scope=$scope AND id=$id AND state=1",
                ("$scope",batch.Scope.Key),("$id",batch.Id),("$state",retry?0:6),("$code",code));
            if(!retry)Execute(db,tx,"UPDATE understanding_work_items SET state=6 WHERE scope=$scope AND batch_id=$id AND state=1",("$scope",batch.Scope.Key),("$id",batch.Id));
            tx.Commit();Log(batch.Id,"work_deferred",code);
        }
    }
    public void PauseWork(ReviewWorkBatch batch,string code,bool newInput=false)
    {
        lock(_gate)
        {
            EnsureSchema();using var db=database.Open();using var tx=db.BeginTransaction();
            var paused=batch with{Attempts=Math.Max(0,batch.Attempts-1)};
            Execute(db,tx,"UPDATE understanding_work_batches SET state=$state,batch_json=$json,code=$code WHERE scope=$scope AND id=$id AND state=1",
                ("$scope",batch.Scope.Key),("$id",batch.Id),("$state",newInput?7:0),("$json",JsonSerializer.Serialize(paused)),("$code",code));
            if(newInput)Execute(db,tx,"UPDATE understanding_work_items SET state=0,batch_id=NULL WHERE scope=$scope AND batch_id=$id AND state=1",("$scope",batch.Scope.Key),("$id",batch.Id));
            tx.Commit();Log(batch.Id,"work_paused",code);
        }
    }
    public void RecoverWork()
    {
        lock(_gate){EnsureSchema();using var db=database.Open();Execute(db,null,"UPDATE understanding_work_batches SET state=0 WHERE state=1");Log("session","work_recovery_ready");}
    }
    public IReadOnlyList<ExperienceSourceHead> IncrementalHeads(ConversationScope scope,long after,int maxItems)
    {
        lock(_gate)
        {
            EnsureSchema();using var db=database.Open();
            using var command=Command(db,null,"SELECT head_json FROM understanding_work_items WHERE scope=$scope AND json_extract(head_json,'$.Source.SourceRevision')>$after ORDER BY json_extract(head_json,'$.Source.SourceRevision') DESC,id DESC LIMIT $limit",
                ("$scope",scope.Key),("$after",after),("$limit",Math.Clamp(maxItems,0,8)));
            using var r=command.ExecuteReader();var heads=new List<ExperienceSourceHead>();while(r.Read())heads.Add(JsonSerializer.Deserialize<ExperienceSourceHead>(r.GetString(0))!);return heads;
        }
    }
}
