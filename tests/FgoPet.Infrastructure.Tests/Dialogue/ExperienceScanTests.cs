using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class ExperienceScanTests
{
    private readonly RuntimeDatabase _database = new(Path.Combine(Path.GetTempPath(),"fgo-scan-"+Guid.NewGuid().ToString("N"),"runtime.db"),pooling:false);
    private readonly ConversationScope _scope = new("mash","p");
    private readonly SqliteExperienceTimeline _timeline;
    public ExperienceScanTests()
    {
        new RuntimeDatabaseMigrator(_database).Migrate();
        _timeline=new(_database,new SqliteExperienceSourceReader(_database));
        _timeline.Boundary(_scope);
        var repository=new SqliteConversationRepository(_database);
        var context=new ContentContextKey("mash","fixture","1.0.0","default","p1","k1");
        repository.CreateConversation("c","mash",context,DateTimeOffset.Parse("2026-10-20T00:00:00Z"),"p","Synthetic");
        for(var i=1;i<=70;i++) repository.Append(new("m"+i,"c","mash",i%2==0?ChatMessageRole.Assistant:ChatMessageRole.User,
            "synthetic😀",ChatMessageStatus.Completed,DateTimeOffset.Parse("2026-10-20T00:00:00Z"),context,i));
    }
    [Fact] public void Shared_revision_cursor_does_not_lose_messages_after_scope_changes_or_restart()
    {
        using(var db=_database.Open()) using(var cmd=db.CreateCommand())
        {cmd.CommandText="UPDATE conversations SET project_label='Renamed' WHERE conversation_id='c'";cmd.ExecuteNonQuery();}
        var cursor=new ExperienceScanCursor(0,"");var ids=new List<string>();bool more;
        do {var page=_timeline.ScanHeads(_scope,cursor,7);Assert.Equal(ExperienceSourceStatus.Available,page.Status);
            ids.AddRange(page.Items.Select(h=>h.Source.MessageId));cursor=page.Cursor;more=page.HasMore;}while(more);
        Assert.Equal(70,ids.Count);Assert.Equal(70,ids.Distinct().Count());
        var reopened=new SqliteExperienceTimeline(_database,new SqliteExperienceSourceReader(_database));
        Assert.Empty(reopened.ScanHeads(_scope,cursor).Items);
        Assert.Equal(cursor.Sequence,reopened.InputRevision(_scope));
    }
    [Fact] public void Exact_scope_and_qualified_sources_only_with_invalid_heads_not_blocking_later_messages()
    {
        using(var db=_database.Open()) using(var cmd=db.CreateCommand())
        {cmd.CommandText="UPDATE chat_messages SET text='' WHERE message_id='m1'; UPDATE chat_messages SET status='failed' WHERE message_id='m2'";cmd.ExecuteNonQuery();}
        Assert.Empty(_timeline.ScanHeads(new("mash","other"),new(0,"")).Items);
        var cursor=new ExperienceScanCursor(0,"");var count=0;bool more;
        do {var page=_timeline.ScanHeads(_scope,cursor,3);count+=page.Items.Count;cursor=page.Cursor;more=page.HasMore;}while(more);
        Assert.Equal(68,count);Assert.Empty(_timeline.ScanHeads(_scope,cursor).Items);
    }
    [Fact] public void New_input_revision_ignores_an_unrelated_scope_and_auto_since_excludes_old_heads()
    {
        var boundary=_timeline.Boundary(_scope);var revision=_timeline.InputRevision(_scope);
        Assert.Empty(_timeline.ScanHeads(_scope,new(boundary,"\uffff")).Items);
        using(var db=_database.Open()) using(var cmd=db.CreateCommand())
        {cmd.CommandText="UPDATE conversations SET project_id='other' WHERE conversation_id='c'";cmd.ExecuteNonQuery();}
        Assert.Equal(0,_timeline.InputRevision(_scope));Assert.True(_timeline.Boundary(_scope)>boundary);
        Assert.Equal(revision,boundary);
    }
    [Fact] public void External_attribution_cannot_be_forged_for_an_assistant_message()
    {
        Assert.Throws<InvalidOperationException>(()=>_timeline.Read(new("c","mash","p"),"m2",FgoPet.Extensibility.ConversationSourceKind.External));
        var reader=new ConversationSourceReader(new SqliteConversationRepository(_database));
        Assert.Throws<InvalidOperationException>(()=>reader.Read(new("c","mash","p"),"m2",FgoPet.Extensibility.ConversationSourceKind.External));
    }
}
