using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class ExperienceTimelineSearchTriggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-source-search-" + Guid.NewGuid().ToString("N"));
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _repository;
    private readonly SqliteExperienceTimeline _timeline;
    private readonly ConversationScope _scope = new("fixture", null);
    private readonly ContentContextKey _context = new("fixture", "fixture", "1.0.0", "default", "p1", "k1");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T00:00:00Z");

    public ExperienceTimelineSearchTriggerTests()
    {
        _database = new(Path.Combine(_root, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _repository = new(_database);
        _repository.CreateConversation("c", "fixture", _context, Now);
        _timeline = new(_database, new SqliteExperienceSourceReader(_database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initializing_timeline_preserves_user_and_assistant_appends(bool hasEarlierMessage)
    {
        if (hasEarlierMessage) Append("earlier", ChatMessageRole.User, "earlier source", 1);
        var boundary = _timeline.Boundary(_scope);
        Append("u", ChatMessageRole.User, "synthetic user 😀", 2);
        var user = _timeline.Read(new("c", "fixture", null), "u", ConversationSourceKind.User);
        Append("a", ChatMessageRole.Assistant, "synthetic assistant", 3);
        var assistant = _timeline.Read(new("c", "fixture", null), "a", ConversationSourceKind.Assistant);

        Assert.Equal("synthetic user 😀", _repository.LoadMessages("c", "fixture").Single(m => m.MessageId == "u").Text);
        Assert.True(user.SourceRevision > boundary);
        Assert.True(assistant.SourceRevision > user.SourceRevision);
        Assert.True(_timeline.IsCurrent(user));
        Assert.True(_timeline.IsCurrent(assistant));
        Assert.Equal(2, Search("synthetic"));
        AssertSearchIntegrity();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_v14_trigger_is_upgraded_without_losing_sources_or_search(bool timelineAlreadyInitialized)
    {
        Append("earlier", ChatMessageRole.User, "preserved original", 1);
        var boundary = timelineAlreadyInitialized ? _timeline.Boundary(_scope) : 0;
        Execute("""
            DROP TRIGGER chat_message_search_update;
            CREATE TRIGGER chat_message_search_update AFTER UPDATE ON chat_messages BEGIN
              INSERT INTO chat_message_search(chat_message_search,rowid,text) VALUES('delete',old.rowid,old.text);
              INSERT INTO chat_message_search(rowid,text) VALUES(new.rowid,new.text);
              UPDATE conversations SET context_revision=context_revision+1 WHERE conversation_id IN (old.conversation_id,new.conversation_id);
            END;
            DELETE FROM schema_migrations WHERE version>14;
            """);
        var migrator = new RuntimeDatabaseMigrator(_database);
        migrator.Migrate();
        var version = Scalar("SELECT MAX(version) FROM schema_migrations");
        migrator.Migrate();
        Assert.Equal(version, Scalar("SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(boundary, timelineAlreadyInitialized ? _timeline.Boundary(_scope) : 0);
        _timeline.Boundary(_scope);
        Append("new", ChatMessageRole.User, "preserved upgraded message", 2);

        Assert.Equal("preserved original", _repository.LoadMessages("c", "fixture").Single(m => m.MessageId == "earlier").Text);
        Assert.Equal(2, Search("preserved"));
        Assert.Equal(1, Search("upgraded"));
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, version);
        AssertSearchIntegrity();
    }

    [Fact]
    public void Status_completion_preserves_search_and_advances_source_and_context_versions()
    {
        _repository.Append(new("pending", "c", "fixture", ChatMessageRole.Assistant, "searchable pending",
            ChatMessageStatus.Pending, Now, _context, 1));
        var boundary = _timeline.Boundary(_scope);
        var contextRevision = Scalar("SELECT context_revision FROM conversations WHERE conversation_id='c'");
        Execute("UPDATE chat_messages SET status='completed' WHERE message_id='pending'");

        var page = _timeline.ReadPage(_scope, "c");
        Assert.Equal(ExperienceSourceStatus.Available, page.Status);
        Assert.Equal("pending", Assert.Single(page.Items).Source.MessageId);
        Assert.True(_timeline.Boundary(_scope) > boundary);
        Assert.True(Scalar("SELECT context_revision FROM conversations WHERE conversation_id='c'") > contextRevision);
        Assert.Equal(1, Search("searchable"));
        AssertSearchIntegrity();
    }

    [Fact]
    public void Text_change_updates_search_and_invalidates_the_previous_source()
    {
        Append("u", ChatMessageRole.User, "obsolete phrase", 1);
        var original = _timeline.Read(new("c", "fixture", null), "u", ConversationSourceKind.User);
        Execute("UPDATE chat_messages SET text='replacement phrase 😀' WHERE message_id='u'");

        Assert.False(_timeline.IsCurrent(original));
        var current = _timeline.Read(new("c", "fixture", null), "u", ConversationSourceKind.User);
        Assert.True(current.SourceRevision > original.SourceRevision);
        Assert.Equal(0, Search("obsolete"));
        Assert.Equal(1, Search("replacement"));
        Assert.Equal("replacement phrase 😀", Assert.Single(_timeline.ReadPage(_scope, "c").Items).Excerpt);
        AssertSearchIntegrity();
    }

    [Fact]
    public void Rolling_back_an_insert_restores_source_clock_search_and_context_revision()
    {
        var boundary = _timeline.Boundary(_scope);
        var contextRevision = Scalar("SELECT context_revision FROM conversations WHERE conversation_id='c'");
        using (var connection = _database.Open())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc,binding_id)
                SELECT 'rolled-back',conversation_id,servant_id,1,'user','rollback fixture','completed',created_at_utc,current_binding_id
                FROM conversations WHERE conversation_id='c';
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT count(*) FROM chat_message_search WHERE chat_message_search MATCH 'rollback'";
            Assert.Equal(1L, command.ExecuteScalar());
        }

        Assert.Empty(_repository.LoadMessages("c", "fixture"));
        Assert.Equal(boundary, _timeline.Boundary(_scope));
        Assert.Equal(contextRevision, Scalar("SELECT context_revision FROM conversations WHERE conversation_id='c'"));
        Assert.Equal(0, Search("rollback"));
        AssertSearchIntegrity();
    }

    [Fact]
    public void Rowid_change_keeps_search_addressed_to_the_original_message()
    {
        Append("u", ChatMessageRole.User, "relocated fixture", 1);
        var original = _timeline.Read(new("c", "fixture", null), "u", ConversationSourceKind.User);
        var previousRowid = Scalar("SELECT rowid FROM chat_messages WHERE message_id='u'");
        Execute("UPDATE chat_messages SET rowid=rowid+100 WHERE message_id='u'");

        Assert.Equal(previousRowid + 100, Scalar("SELECT rowid FROM chat_message_search WHERE chat_message_search MATCH 'relocated'"));
        Assert.True(_timeline.IsCurrent(original));
        AssertSearchIntegrity();
    }

    private void Append(string id, ChatMessageRole role, string text, int sequence) =>
        _repository.Append(new(id, "c", "fixture", role, text, ChatMessageStatus.Completed, Now, _context, sequence));

    private void Execute(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private int Search(string term)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM chat_message_search WHERE chat_message_search MATCH $term";
        command.Parameters.AddWithValue("$term", term);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void AssertSearchIntegrity() => Execute("INSERT INTO chat_message_search(chat_message_search,rank) VALUES('integrity-check',1)");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
