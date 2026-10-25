using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Testing.Understanding;

/// <summary>Isolated fixed-version external fixture over the published source port; never a production connector.</summary>
internal sealed class FixtureSources:IExperienceTimeline,IExperienceSourceReader
{
    private readonly IExperienceTimeline _inner;
    private readonly IExperienceSourceReader _originals;
    private readonly ConversationScope _scope;
    internal ExperienceSourceHead? Document{get;}
    private const string Text="max_retries = 2";
    internal FixtureSources(IExperienceTimeline inner,IExperienceSourceReader originals,ConversationScope scope,bool external)
    {
        _inner=inner;_originals=originals;_scope=scope;
        if(!external)return;
        var at=new DateTimeOffset(2026,10,20,0,0,0,TimeSpan.Zero);
        var version=ValidationHost.Digest("synthetic-config|v1|synthetic-maintainer|2026-10-20|"+Text);
        var source=new ConversationSourceSnapshot(new("external-document",scope.ServantId,scope.ProjectId),"config-v1",version,at,
            ConversationSourceKind.External,"Synthetic P4",1,"fixture-config@v1","Synthetic configuration maintainer");
        Document=new(new("external-document","config-v1",1,at),source,Text.Length);
    }
    public long Boundary(ConversationScope scope)=>_inner.Boundary(scope);
    public long InputRevision(ConversationScope scope)=>_inner.InputRevision(scope)^(Document is null?0:1L<<62);
    public ExperienceScanPage ScanHeads(ConversationScope scope,ExperienceScanCursor cursor,int maxItems=32,CancellationToken token=default)=>_inner.ScanHeads(scope,cursor,maxItems,token);
    public ExperienceWindowPage ReadPage(ConversationScope scope,string conversationId,int startSequence=1,ExperienceWindowCursor? cursor=null,int maxItems=8,int maxScalars=6000,CancellationToken token=default)=>
        _inner.ReadPage(scope,conversationId,startSequence,cursor,maxItems,maxScalars,token);
    public ExperienceWindowPage ReadAdjacent(ConversationScope scope,HistoryAnchor anchor,CancellationToken token=default)=>_inner.ReadAdjacent(scope,anchor,token);
    public ConversationSourceSnapshot Read(ToolScope scope,string id,ConversationSourceKind kind)
    {
        if(kind==ConversationSourceKind.External)
            return Document is{} head&&head.Source.Scope==scope&&head.Source.MessageId==id?head.Source:throw new InvalidOperationException("EXPERIENCE_SOURCE_CHANGED");
        return _inner.Read(scope,id,kind);
    }
    public bool IsCurrent(ConversationSourceSnapshot source)=>source.Kind==ConversationSourceKind.External?source==Document?.Source:_inner.IsCurrent(source);
    public ExperienceSourceStatus ValidateSource(ConversationSourceSnapshot source)=>source.Kind==ConversationSourceKind.External?
        IsCurrent(source)?ExperienceSourceStatus.Available:ExperienceSourceStatus.Changed:_inner.ValidateSource(source);
    public ExperienceSourceRead Read(ConversationScope scope,HistoryAnchor anchor,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(anchor.ConversationId!="external-document")return _originals.Read(scope,anchor,token);
        return Document is{} head&&scope==_scope&&head.Anchor==anchor
            ?new(ExperienceSourceStatus.Available,new(scope,anchor,ConversationSourceKind.External,Text,head.Source.Fingerprint,head.Source.SourceLabel,head.Source.Author))
            :new(ExperienceSourceStatus.ScopeDenied);
    }
}

internal sealed class FixtureRecall(IConversationRecallRepository inner,FixtureSources sources):IConversationRecallRepository
{
    private HistoryHit? Document=>sources.Document is{} head?new(head.Anchor,"fixture-config@v1","Fixed synthetic version; use experience.read for the exact original.",true):null;
    public IReadOnlyList<HistoryHit> Browse(ConversationScope scope,string excludedConversationId,int limit=10)=>
        inner.Browse(scope,excludedConversationId,limit).Concat(Document is{} document?[document]:[]).Take(limit).ToArray();
    public IReadOnlyList<HistoryHit> Discover(ConversationScope scope,string excludedConversationId,string query,int limit=20)=>
        inner.Discover(scope,excludedConversationId,query,limit).Concat(Document is{} document?[document]:[]).Take(limit).ToArray();
    public HistoryReadPage Read(ConversationScope scope,HistoryAnchor anchor,int maxChars=6000,HistoryReadCursor? cursor=null)=>inner.Read(scope,anchor,maxChars,cursor);
}
