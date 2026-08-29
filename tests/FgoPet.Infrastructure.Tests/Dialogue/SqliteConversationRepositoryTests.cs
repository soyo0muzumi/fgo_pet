using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteConversationRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-phase3-conversation-{Guid.NewGuid():N}.db");

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
    public void Invalid_conversation_id_does_not_leave_a_database_row()
    {
        var database = new RuntimeDatabase(_path);
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
