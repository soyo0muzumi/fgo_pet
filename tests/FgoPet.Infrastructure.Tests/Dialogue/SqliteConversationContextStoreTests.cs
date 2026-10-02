using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteConversationContextStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-compaction-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteConversationContextStore _store;
    private static readonly ConversationScope Scope = new("mash", "a");
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    public SqliteConversationContextStoreTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database); _store = new(_database);
        _conversations.CreateConversation("c", "mash", Key, DateTimeOffset.UtcNow, "a", "项目 A");
        for (var i = 1; i <= 8; i++) Append(i);
    }

    [Fact]
    public void Atomic_projection_reopens_with_kept_exchanges_and_seam_only_once()
    {
        // Hermes test_in_place_here_n_stores_the_kept_exchanges / folds_the_seam_once.
        var source = _store.Read(Scope, "c");
        Assert.True(_store.TryCommit(Commit(source)));
        Assert.False(_store.TryCommit(Commit(source)));
        var reopened = new SqliteConversationContextStore(new RuntimeDatabase(_path, pooling: false)).Read(Scope, "c");
        Assert.Equal("fixture summary", reopened.Summary!.SummaryText);
        Assert.Equal(new[] { "m5", "m6", "m7", "m8" }, reopened.UncoveredMessages.Select(message => message.MessageId));
        Assert.Equal(8, _conversations.LoadMessages("c", "mash").Count);
        Append(9);
        Assert.NotNull(_store.Read(Scope, "c").Summary);
        Assert.Equal("m9", _store.Read(Scope, "c").UncoveredMessages.Last().MessageId);
    }

    [Fact]
    public void Failure_between_summary_insert_and_pointer_update_rolls_back_everything()
    {
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_projection BEFORE INSERT ON conversation_contexts BEGIN SELECT RAISE(ABORT,'fixture'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => _store.TryCommit(Commit(_store.Read(Scope, "c"))));
        Assert.Null(_conversations.LoadSummary("c", "mash"));
        Assert.Null(_store.Read(Scope, "c").Summary);
    }

    [Fact]
    public void Concurrent_append_delete_and_scope_change_reject_stale_commit()
    {
        var source = _store.Read(Scope, "c");
        Append(9);
        Assert.False(_store.TryCommit(Commit(source)));
        Assert.Throws<KeyNotFoundException>(() => _store.Read(new("mash", "b"), "c"));
        var current = _store.Read(Scope, "c");
        _conversations.DeleteConversation("c", "mash");
        Assert.False(_store.TryCommit(Commit(current)));
    }

    [Fact]
    public void Legacy_summary_is_inactive_and_edits_to_covered_prefix_invalidate_active_summary()
    {
        _conversations.SaveSummary(Commit(_store.Read(Scope, "c")).Summary);
        Assert.Null(_store.Read(Scope, "c").Summary);
        Assert.True(_store.TryCommit(Commit(_store.Read(Scope, "c"))));
        var active = _store.Read(Scope, "c").Summary!;
        Assert.Throws<InvalidOperationException>(() => _conversations.SaveSummary(active));
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE chat_messages SET text='corrected' WHERE message_id='m1'";
            command.ExecuteNonQuery();
        }
        var invalidated = _store.Read(Scope, "c");
        Assert.Null(invalidated.Summary);
        Assert.Equal(8, invalidated.UncoveredMessages.Count);
    }

    private static CompactionCommit Commit(ConversationContextSnapshot source) => new(source,
        new("summary-" + Guid.NewGuid().ToString("N"), "c", "mash", "fixture summary", 4, "m4", Key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("test", "endpoint", "m", "1"), 1000, 500);
    private void Append(int sequence) => _conversations.Append(new("m" + sequence, "c", "mash",
        sequence % 2 == 1 ? ChatMessageRole.User : ChatMessageRole.Assistant, "raw turn " + sequence,
        ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, sequence));
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }
}
