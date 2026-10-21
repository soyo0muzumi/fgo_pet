using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class ExperienceTimelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-timeline-" + Guid.NewGuid().ToString("N"));
    private readonly RuntimeDatabase _database;
    private readonly SqliteExperienceTimeline _timeline;
    private readonly ConversationScope _scope = new("mash", "p");
    public ExperienceTimelineTests()
    {
        _database = new(Path.Combine(_root, "runtime.db"), pooling: false); new RuntimeDatabaseMigrator(_database).Migrate();
        _timeline = new(_database, new SqliteExperienceSourceReader(_database));
        new SqliteConversationRepository(_database).CreateConversation("c", "mash",
            new("mash", "fixture", "1.0.0", "default", "p1", "k1"), DateTimeOffset.Parse("2026-10-18T00:00:00Z"), "p", "Synthetic");
        using var db = _database.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc) VALUES
              ('u','c','mash',1,'user','计划😀修订','completed','2026-10-18T00:00:00+00:00'),
              ('a','c','mash',2,'assistant','真实完成回复','completed','2026-10-18T00:01:00+00:00'),
              ('partial','c','mash',3,'assistant','partial only','failed','2026-10-18T00:02:00+00:00');
            UPDATE chat_messages SET binding_id=(SELECT current_binding_id FROM conversations WHERE conversation_id='c');
            """;
        cmd.ExecuteNonQuery();
    }
    [Fact] public void Fixed_window_preserves_speaker_unicode_coverage_and_rejects_changed_originals()
    {
        var page = _timeline.ReadPage(_scope, "c", maxItems: 1, maxScalars: 3);
        Assert.Equal(ExperienceSourceStatus.Available, page.Status);
        var item = Assert.Single(page.Items); Assert.Equal("计划😀", item.Excerpt);
        Assert.Equal(3, item.IncludedScalars); Assert.Equal(5, item.TotalScalars); Assert.False(item.Complete);
        var validator = _timeline;
        Assert.True(validator.IsCurrent(item.Source));
        var second = _timeline.ReadPage(_scope, "c", cursor: page.Next);
        Assert.Equal("a", Assert.Single(second.Items).Source.MessageId); Assert.Null(second.Next);
        using var db = _database.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE chat_messages SET text='changed' WHERE message_id='u'"; cmd.ExecuteNonQuery();
        Assert.Equal(ExperienceSourceStatus.Changed, _timeline.ReadPage(_scope, "c", cursor: page.Next).Status);
        Assert.False(validator.IsCurrent(item.Source));
    }
    [Fact] public void Wrong_scope_deleted_unavailable_and_cancelled_are_distinct_from_empty()
    {
        Assert.Equal(ExperienceSourceStatus.ScopeDenied, _timeline.ReadPage(new("mash", "other"), "c").Status);
        Assert.Equal(ExperienceSourceStatus.Deleted, _timeline.ReadPage(_scope, "missing").Status);
        Assert.Empty(_timeline.ReadPage(_scope, "c", startSequence: 8).Items);
        Assert.Equal(ExperienceSourceStatus.Available, _timeline.ReadPage(_scope, "c", startSequence: 8).Status);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => _timeline.ReadPage(_scope, "c", token: cts.Token));
        Assert.Equal(3, _timeline.Boundary(_scope));
    }
    [Fact] public void Source_boundary_is_monotonic_across_deletion_and_completion_of_a_preexisting_partial()
    {
        var before = _timeline.Boundary(_scope);
        using var db = _database.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM chat_messages WHERE message_id='a'"; cmd.ExecuteNonQuery();
        var deleted = _timeline.Boundary(_scope); Assert.True(deleted > before);
        cmd.CommandText = "UPDATE chat_messages SET status='completed' WHERE message_id='partial'"; cmd.ExecuteNonQuery();
        var completed = _timeline.Boundary(_scope); Assert.True(completed > deleted);
        Assert.Equal(completed, _timeline.ReadPage(_scope, "c").Items.Single(i => i.Source.MessageId == "partial").SourceSequence);
    }
    [Fact] public void Completed_failed_completed_with_identical_body_invalidates_old_source_revision()
    {
        var old = _timeline.ReadPage(_scope, "c").Items.Single(i => i.Source.MessageId == "u").Source;
        Assert.True(_timeline.IsCurrent(old));
        using var db = _database.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE chat_messages SET status='failed' WHERE message_id='u'; UPDATE chat_messages SET status='completed' WHERE message_id='u'";
        cmd.ExecuteNonQuery();
        Assert.False(_timeline.IsCurrent(old));
        var current = _timeline.ReadPage(_scope, "c").Items.Single(i => i.Source.MessageId == "u").Source;
        Assert.Equal(old.Fingerprint, current.Fingerprint); Assert.True(current.SourceRevision > old.SourceRevision);
    }
    [Fact] public void Moving_conversation_scope_away_and_back_cannot_revive_an_old_source_reference()
    {
        var old = _timeline.ReadPage(_scope, "c").Items[0].Source;
        using var db = _database.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET project_id='other' WHERE conversation_id='c'; UPDATE conversations SET project_id='p' WHERE conversation_id='c'";
        cmd.ExecuteNonQuery(); Assert.False(_timeline.IsCurrent(old));
    }
    [Fact] public void Source_clock_preserves_normal_append_and_rolls_back_with_original_message_transaction()
    {
        var old = _timeline.ReadPage(_scope, "c").Items[0].Source;
        var repository = new SqliteConversationRepository(_database);
        var context = new ContentContextKey("mash", "fixture", "1.0.0", "default", "p1", "k1");
        repository.Append(new("new-user", "c", "mash", ChatMessageRole.User, "new original", ChatMessageStatus.Completed,
            DateTimeOffset.Parse("2026-10-18T00:03:00Z"), context, 4));
        Assert.Equal("new original", repository.LoadMessages("c", "mash").Single(m => m.MessageId == "new-user").Text);
        Assert.True(_timeline.IsCurrent(old)); var boundary = _timeline.Boundary(_scope);
        using (var db = _database.Open())
        using (var tx = db.BeginTransaction())
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE chat_messages SET text='must roll back' WHERE message_id='u'"; cmd.ExecuteNonQuery();
        }
        Assert.Equal(boundary, _timeline.Boundary(_scope)); Assert.True(_timeline.IsCurrent(old));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
