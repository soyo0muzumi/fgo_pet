using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class CompactedHistoryRepositoryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"fgo-current-history-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase database;
    private readonly SqliteConversationRepository conversations;
    private readonly SqliteConversationContextStore contexts;
    private readonly SqliteCompactedHistoryRepository history;
    private static readonly ToolScope Scope = new("c", "mash", "a");
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    private const string LongText = "omitted-detail 😀😀 精确 100%_\" OR *";

    public CompactedHistoryRepositoryTests()
    {
        database = new(path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        conversations = new(database); contexts = new(database); history = new(database);
        conversations.CreateConversation("c", "mash", Key, DateTimeOffset.UtcNow, "a", "A");
        Append("m1", 1, LongText, ChatMessageRole.User, ChatMessageStatus.Completed);
        Append("m2", 2, "omitted-detail failed answer", ChatMessageRole.Assistant, ChatMessageStatus.Failed);
        Append("m3", 3, "omitted-detail cancelled user", ChatMessageRole.User, ChatMessageStatus.Cancelled);
        Append("m4", 4, "omitted-detail cancelled answer", ChatMessageRole.Assistant, ChatMessageStatus.Cancelled);
        Append("m5", 5, "omitted-detail active tail", ChatMessageRole.User, ChatMessageStatus.Completed);
    }

    [Fact]
    public void Omitted_detail_is_retrievable_only_after_compaction_with_original_status_and_sources()
    {
        Assert.Empty(history.Search(Scope, "omitted-detail").Items);
        Compact();
        var page = history.Search(Scope, "omitted-detail");
        Assert.Equal(4, page.CoveredThroughSequence);
        Assert.Equal(new[] { "m1", "m2", "m3", "m4" }, page.Items.Select(x => x.MessageId));
        Assert.Equal(new[] { "completed", "failed", "cancelled", "cancelled" }, page.Items.Select(x => x.Status));
        Assert.Equal("user", page.Items[0].Role);
        Assert.Equal(LongText, page.Items[0].Text);
        Assert.All(page.Items, item => Assert.NotEqual(default, item.CreatedAtUtc));
    }

    [Fact]
    public void Search_is_literal_and_pages_in_sequence_order()
    {
        Compact();
        Assert.Equal("m1", Assert.Single(history.Search(Scope, "100%_\" OR *").Items).MessageId);
        Assert.Empty(history.Search(Scope, "' OR 1=1 --").Items);
        var first = history.Search(Scope, "omitted-detail", maxItems: 2);
        Assert.Equal(2, first.NextSequence);
        var second = history.Search(Scope, "omitted-detail", first.NextSequence!.Value, maxItems: 2);
        Assert.Equal(new[] { "m3", "m4" }, second.Items.Select(x => x.MessageId));
        Assert.Null(second.NextSequence);
    }

    [Fact]
    public void Read_reconstructs_unicode_original_without_half_surrogates_or_tail_access()
    {
        Compact();
        var offset = 0;
        var recovered = new StringBuilder();
        do
        {
            var item = Assert.Single(history.Read(Scope, "m1", offset, maxChars: 1).Items);
            Assert.Single(item.Text.EnumerateRunes());
            recovered.Append(item.Text);
            if (item.NextOffset is null) break;
            Assert.True(item.NextOffset > offset);
            offset = item.NextOffset.Value;
        } while (true);
        Assert.Equal(LongText, recovered.ToString());
        Assert.Empty(history.Read(Scope, "m5").Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Read(Scope, "m1", int.MaxValue));
    }

    [Fact]
    public void Forged_scope_deleted_source_and_stale_coverage_fail_closed()
    {
        Compact();
        foreach (var forged in new[] { Scope with { RoleId = "other" }, Scope with { ProjectId = "b" }, Scope with { ConversationId = "other" } })
        {
            Assert.Throws<KeyNotFoundException>(() => history.Search(forged, "omitted-detail"));
            Assert.Throws<KeyNotFoundException>(() => history.Read(forged, "m1"));
        }
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE chat_messages SET text='edited' WHERE message_id='m1'";
            command.ExecuteNonQuery();
        }
        Assert.Empty(history.Search(Scope, "omitted-detail").Items);
        Assert.Empty(history.Read(Scope, "m2").Items);
        conversations.DeleteConversation("c", "mash");
        Assert.Throws<KeyNotFoundException>(() => history.Read(Scope, "m1"));
    }

    [Fact]
    public void Cancellation_and_limits_are_enforced_at_repository_boundary()
    {
        Compact();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => history.Search(Scope, "omitted-detail", cancellationToken: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => history.Read(Scope, "m1", cancellationToken: cancel.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Search(Scope, "x", maxItems: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Read(Scope, "m1", maxChars: 6001));
        var bounded = history.Search(Scope, "omitted-detail", maxChars: 2);
        Assert.Equal(2, bounded.Items.Sum(x => x.Text.EnumerateRunes().Count()));
        Assert.NotNull(bounded.Items[0].NextOffset);
        Assert.NotNull(bounded.NextSequence);
    }

    private void Compact()
    {
        var source = contexts.Read(new("mash", "a"), "c");
        Assert.True(contexts.TryCommit(new(source,
            new("s", "c", "mash", "A summary deliberately omitting the detail", 4, "m4", Key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("test", "endpoint", "m", "1"), 1000, 500)));
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public void Nul_and_unicode_originals_remain_searchable_and_readable_in_database_encoding(string encoding)
    {
        var encodedPath = path + ".encoded";
        try
        {
            var encodedDatabase = new RuntimeDatabase(encodedPath, pooling: false);
            using (var connection = encodedDatabase.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA encoding='{encoding}'";
                command.ExecuteNonQuery();
            }
            new RuntimeDatabaseMigrator(encodedDatabase).Migrate();
            var source = new SqliteConversationRepository(encodedDatabase);
            var store = new SqliteConversationContextStore(encodedDatabase);
            var repository = new SqliteCompactedHistoryRepository(encodedDatabase);
            source.CreateConversation("c", "mash", Key, DateTimeOffset.UtcNow, "a", "A");
            var original = string.Concat(Enumerable.Repeat("😀前文", 600)) + "\0after-nul 后文 🧭";
            source.Append(new("nul-user", "c", "mash", ChatMessageRole.User, original, ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, 1));
            source.Append(new("nul-answer", "c", "mash", ChatMessageRole.Assistant, "answer", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, 2));
            source.Append(new("nul-tail", "c", "mash", ChatMessageRole.User, "next", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, 3));
            Assert.Equal(original, source.LoadMessages("c", "mash")[0].Text);
            Assert.True(store.TryCommit(new(store.Read(new("mash", "a"), "c"),
                new("nul-summary", "c", "mash", "Omitted original", 2, "nul-answer", Key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("test", "endpoint", "m", "1"), 1000, 500)));
            var hit = Assert.Single(repository.Search(Scope, "after-nul").Items);
            Assert.Equal("nul-user", hit.MessageId);
            Assert.Contains("\0after-nul 后文 🧭", hit.Text);
            var recovered = new StringBuilder();
            var offset = 0;
            while (true)
            {
                var item = Assert.Single(repository.Read(Scope, "nul-user", offset, maxChars: 127).Items);
                recovered.Append(item.Text);
                if (item.NextOffset is null) break;
                Assert.True(item.NextOffset > offset);
                offset = item.NextOffset.Value;
            }
            Assert.Equal(original, recovered.ToString());
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(encodedPath + suffix);
        }
    }
    private void Append(string id, int sequence, string text, ChatMessageRole role, ChatMessageStatus status) =>
        conversations.Append(new(id, "c", "mash", role, text, status, DateTimeOffset.UtcNow, Key, sequence));
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
}
