using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class ReviewWorkQueueTests
{
    private readonly RuntimeDatabase _db=new(Path.Combine(Path.GetTempPath(),"fgo-work-"+Guid.NewGuid().ToString("N"),"runtime.db"),pooling:false);
    private readonly ConversationScope _scope=new("mash","p");
    private readonly Timeline _sources=new();
    private readonly UnderstandingService _owner;
    public ReviewWorkQueueTests(){new RuntimeDatabaseMigrator(_db).Migrate();_owner=new(_db,_sources);}
    private void Enable()=>_owner.Configure(_scope,_owner.State(_scope).Revision,new(UnderstandingMode.Proposals,ReadSources:true,SaveCandidates:true,ExternalProcessing:true));
    [Fact] public void Queue_capacity_and_missing_notifications_do_not_lose_eligible_intervals()
    {
        Enable();_sources.Add(35);var seen=new HashSet<string>();
        for(var turn=0;turn<100;turn++)
        {
            _owner.Accumulate(_sources,_scope,capacity:2);
            while(_owner.ClaimWork(_scope) is {} batch)
            {foreach(var item in batch.Items)Assert.True(seen.Add(item.Head.Source.MessageId));_owner.FinishWork(batch,new(batch.Id,[new("read-set",UnderstandingOutcome.NoChange)]));}
            if(seen.Count==35)break;
        }
        Assert.Equal(35,seen.Count);Assert.Equal(0,_owner.WorkSummary(_scope).PendingSources);
        _owner.Accumulate(_sources,_scope,capacity:2);Assert.Null(_owner.ClaimWork(_scope));
    }
    [Fact] public void Crash_recovery_keeps_exact_batch_identity_and_completed_ranges_do_not_repeat()
    {
        Enable();_sources.Add(4);_owner.Accumulate(_sources,_scope);var first=_owner.ClaimWork(_scope);Assert.NotNull(first);
        var reopened=new UnderstandingService(_db,_sources);reopened.RecoverWork();var recovered=reopened.ClaimWork(_scope);
        Assert.NotNull(recovered);Assert.Equal(first.Id,recovered.Id);
        reopened.FinishWork(recovered,new(recovered.Id,[new("a",UnderstandingOutcome.Applied),new("b",UnderstandingOutcome.ProposedPending)]));
        reopened.RecoverWork();Assert.Null(reopened.ClaimWork(_scope));
        Assert.Contains(reopened.WorkSummary(_scope).Batches,b=>b.State==ReviewWorkState.ProposedPending);
    }
    [Fact] public void Off_then_enabled_excludes_old_failed_and_unprocessed_ranges()
    {
        _sources.Add(3);Assert.False(_owner.Accumulate(_sources,_scope));Assert.Null(_owner.ClaimWork(_scope));
        _owner.Configure(_scope,0,new(UnderstandingMode.Proposals,ReadSources:true,SaveCandidates:true,ExternalProcessing:true),3);
        _sources.Add(2);_owner.Accumulate(_sources,_scope);var batch=_owner.ClaimWork(_scope);Assert.NotNull(batch);
        Assert.All(batch.Items,i=>Assert.True(i.Head.Source.SourceRevision>3));Assert.Equal(2,batch.Items.Count);
    }
    [Fact] public void Large_original_advances_only_its_actually_reviewed_scalar_slices()
    {
        Enable();_sources.Add(1,5000);_owner.Accumulate(_sources,_scope);var total=0;var offsets=new List<int>();
        while(_owner.ClaimWork(_scope) is {} batch)
        {var item=Assert.Single(batch.Items);offsets.Add(item.StartScalar);total+=item.IncludedScalars;_owner.FinishWork(batch,new(batch.Id,[new("read-set",UnderstandingOutcome.NoChange)]));}
        Assert.Equal(5000,total);Assert.Equal([0,2000,4000],offsets);
    }
    [Fact] public void Policy_reenable_cannot_let_suppressed_old_items_fill_the_queue_forever()
    {
        Enable();_sources.Add(3);_owner.Accumulate(_sources,_scope,capacity:2);
        _owner.Configure(_scope,_owner.State(_scope).Revision,new());
        _owner.Configure(_scope,_owner.State(_scope).Revision,new(UnderstandingMode.Proposals,ReadSources:true,SaveCandidates:true,ExternalProcessing:true),3);
        _sources.Add(2);Assert.True(_owner.Accumulate(_sources,_scope,capacity:2));
        var work=_owner.ClaimWork(_scope);Assert.NotNull(work);Assert.Equal(2,work.Items.Count);Assert.All(work.Items,i=>Assert.True(i.Head.Source.SourceRevision>3));
    }
    internal sealed class Timeline:IExperienceTimeline,IExperienceSourceReader
    {
        public readonly List<ExperienceSourceHead> Heads=[];public bool Current=true;public int BodyReads;
        public void Add(int count,int scalars=10)
        {
            for(var i=0;i<count;i++){var n=Heads.Count+1;var source=new ConversationSourceSnapshot(new("c","mash","p"),"m"+n,new('A',64),
                DateTimeOffset.Parse("2026-10-20T00:00:00Z"),n%2==0?ConversationSourceKind.Assistant:ConversationSourceKind.User,"Synthetic",n);
                Heads.Add(new(new("c",source.MessageId,n,source.OccurredAtUtc),source,scalars));}
        }
        public long Boundary(ConversationScope scope)=>Heads.Count;
        public long InputRevision(ConversationScope scope)=>Heads.Count;
        public ExperienceScanPage ScanHeads(ConversationScope scope,ExperienceScanCursor cursor,int maxItems=32,CancellationToken token=default)
        {var items=Heads.Where(h=>h.Source.SourceRevision>cursor.Sequence).Take(maxItems).ToArray();return new(ExperienceSourceStatus.Available,items,
            items.Length==0?cursor:new(items[^1].Source.SourceRevision,items[^1].Source.MessageId),Heads.Any(h=>h.Source.SourceRevision>(items.LastOrDefault()?.Source.SourceRevision??cursor.Sequence)));}
        public ExperienceSourceRead Read(ConversationScope scope,HistoryAnchor anchor,CancellationToken token=default)
        {BodyReads++;var head=Heads.Single(h=>h.Anchor==anchor);return new(ExperienceSourceStatus.Available,new(scope,anchor,head.Source.Kind,new string('x',head.TotalScalars),head.Source.Fingerprint));}
        public ConversationSourceSnapshot Read(ToolScope scope,string id,ConversationSourceKind kind)=>Heads.Single(h=>h.Source.MessageId==id).Source;
        public bool IsCurrent(ConversationSourceSnapshot source)=>Current&&Heads.Any(h=>h.Source==source);
        public ExperienceWindowPage ReadPage(ConversationScope scope,string conversationId,int startSequence=1,ExperienceWindowCursor? cursor=null,int maxItems=8,int maxScalars=6000,CancellationToken token=default)=>throw new NotSupportedException();
        public ExperienceWindowPage ReadAdjacent(ConversationScope scope,HistoryAnchor anchor,CancellationToken token=default)=>throw new NotSupportedException();
    }
}
