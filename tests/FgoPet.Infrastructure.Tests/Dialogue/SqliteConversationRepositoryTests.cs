using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteConversationRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-phase3-conversation-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void History_pages_are_bounded_stable_and_do_not_hydrate_large_message_bodies(bool compactTimestamp)
    {
        var repository = CreateRepository();
        using (var connection = new RuntimeDatabase(_path, pooling: false).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<2000)
                INSERT INTO conversations(conversation_id, servant_id, created_at_utc, updated_at_utc, status)
                SELECT printf('c-%04d', i), '800100', $time, $time, 'active' FROM n;
                INSERT INTO chat_messages(message_id, conversation_id, servant_id, sequence, role, text, status, created_at_utc)
                SELECT 'm-' || conversation_id, conversation_id, servant_id, 1, 'user', $body, 'completed', $time FROM conversations;
                """;
            command.Parameters.AddWithValue("$time", compactTimestamp ? "2026-08-29T00:00:00Z" : Now().ToString("O"));
            command.Parameters.AddWithValue("$body", "Title\n" + new string('x', 11_900));
            command.ExecuteNonQuery();
        }
        repository.CreateConversation("other-role", "100001", Context("casual", "100001"), Now());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = repository.ReadPage("800100");
        clock.Stop();
        Assert.Equal(50, first.Items.Count);
        Assert.Equal("c-2000", first.Items[0].ConversationId);
        Assert.All(first.Items, item => { Assert.Equal(50, item.Title.Length); Assert.DoesNotContain('\n', item.Title); });
        Assert.NotNull(first.Next);
        // Missing bindings make full message hydration fail; title projection
        // must still succeed. Deleting the cursor row must not skip another row.
        Assert.Throws<InvalidDataException>(() => repository.LoadMessages("c-2000", "800100"));
        repository.DeleteConversation(first.Next!.ConversationId, "800100");
        var second = repository.ReadPage("800100", before: first.Next);
        Assert.Equal("c-1950", second.Items[0].ConversationId);
        Assert.Empty(first.Items.Select(item => item.ConversationId).Intersect(second.Items.Select(item => item.ConversationId)));
        Assert.Throws<ArgumentException>(() => repository.ReadPage("100001", before: first.Next));
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.ReadPage("800100", 51));
        Console.WriteLine($"History metadata projection: 2000 conversations / 24 MB bodies, first page {clock.Elapsed.TotalMilliseconds:F2} ms.");
    }

    [Fact]
    public void History_without_user_messages_has_a_readable_fallback_and_exhausted_cursor()
    {
        var repository = CreateRepository();
        repository.CreateConversation("empty", "800100", Context("casual"), Now());
        var page = repository.ReadPage("800100");
        Assert.Equal("新会话", Assert.Single(page.Items).Title);
        Assert.Null(page.Next);
        Assert.Empty(repository.ReadPage("another").Items);
    }

    [Fact]
    public void Append_and_load_are_isolated_by_servant_id()
    {
        var repository = CreateRepository();
        var context = Context("casual");
        repository.CreateConversation("c1", "800100", context, Now());
        var otherContext = Context("casual", "100001");
        repository.CreateConversation("c2", "100001", otherContext, Now());
        repository.Append(Message("m1", "c1", "800100", 1, ChatMessageRole.User, "你好", context));
        repository.Append(Message("m2", "c2", "100001", 1, ChatMessageRole.User, "你好", otherContext));

        Assert.Single(repository.LoadMessages("c1", "800100"));
        Assert.Empty(repository.LoadMessages("c1", "100001"));
    }

    [Fact]
    public void Load_preserves_context_used_by_the_message()
    {
        var repository = CreateRepository();
        var context = Context("casual");
        repository.CreateConversation("c1", "800100", context, Now());
        repository.Append(Message("m1", "c1", "800100", 1, ChatMessageRole.User, "开始工作", context));

        var message = Assert.Single(repository.LoadMessages("c1", "800100"));

        Assert.Equal("casual", message.ContentContext.AppearanceId);
        Assert.Equal("persona-2", message.ContentContext.PersonaVersion);
    }

    [Fact]
    public void Delete_conversation_removes_its_messages()
    {
        var repository = CreateRepository();
        var context = Context("casual");
        repository.CreateConversation("c1", "800100", context, Now());
        repository.Append(Message("m1", "c1", "800100", 1, ChatMessageRole.User, "你好", context));

        repository.DeleteConversation("c1", "800100");

        Assert.Empty(repository.LoadMessages("c1", "800100"));
    }

    [Fact]
    public void Runtime_state_round_trips_and_can_be_deleted()
    {
        var repository = CreateRepository();
        repository.WriteState("LastActiveConversationId:800100", "c1", Now());

        Assert.Equal("c1", repository.ReadState("LastActiveConversationId:800100"));
        repository.DeleteState("LastActiveConversationId:800100");
        Assert.Null(repository.ReadState("LastActiveConversationId:800100"));
    }

    [Fact]
    public void Invalid_conversation_id_does_not_leave_a_database_row()
    {
        var database = new RuntimeDatabase(_path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var repository = new SqliteConversationRepository(database);

        Assert.Throws<ArgumentException>(() => repository.CreateConversation(
            " ", "800100", Context("casual"), Now()));

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversations";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private SqliteConversationRepository CreateRepository()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return new SqliteConversationRepository(database);
    }

    private static ChatMessage Message(
        string messageId,
        string conversationId,
        string servantId,
        int sequence,
        ChatMessageRole role,
        string text,
        ContentContextKey context) =>
        new(messageId, conversationId, servantId, role, text, ChatMessageStatus.Completed, Now(), context, sequence);

    private static ContentContextKey Context(string appearanceId, string servantId = "800100") =>
        new(servantId, "official.mash", "1.1.0", appearanceId, "persona-2", "knowledge-1");

    private static DateTimeOffset Now() => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
}
