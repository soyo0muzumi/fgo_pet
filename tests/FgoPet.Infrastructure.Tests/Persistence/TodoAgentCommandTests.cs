using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class TodoAgentCommandTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "fgopet-todo-native-" + Guid.NewGuid().ToString("N") + ".db");
    private RuntimeDatabase Database()
    {
        var db = new RuntimeDatabase(path, pooling: false);
        new RuntimeDatabaseMigrator(db).Migrate();
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO conversations(conversation_id,servant_id,created_at_utc,updated_at_utc,status) VALUES('conversation','role','2026-10-06','2026-10-06','active')";
        command.ExecuteNonQuery();
        return db;
    }
    private static TodoItem Item() => new("native-todo", "fixture", null, TodoPriority.Normal, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private static TodoAgentCommit Mutation(TodoItem? expected, TodoItem replacement) => new(new("conversation", "role", null), new string('A', 64), new string('B', 64), expected, replacement);

    [Fact]
    public void Create_receipt_and_effect_commit_atomically_and_survive_reopen()
    {
        var db = Database();
        var repo = new SqliteTodoRepository(db);
        var command = Mutation(null, Item());
        var result = ((ITodoAgentCommandRepository)repo).CommitAgentCommand(command);
        Assert.Equal(TodoAgentCommitKind.Committed, result.Kind);
        Assert.Equal(TodoItemVersion.Of(Item()), result.Version);
        var retry = ((ITodoAgentCommandRepository)new SqliteTodoRepository(db)).CommitAgentCommand(command);
        Assert.Equal(TodoAgentCommitKind.AlreadyCommitted, retry.Kind);
        Assert.Single(repo.List());
        Assert.Equal(TodoAgentCommitKind.Conflict, ((ITodoAgentCommandRepository)repo).CommitAgentCommand(command with { Fingerprint = new string('C', 64) }).Kind);
        var port = (ITodoAgentCommandRepository)new SqliteTodoRepository(db);
        Assert.Equal(result.Version, port.ReadAgentReceipt(command.Scope, command.IdempotencyKey, command.Fingerprint)?.Version);
        Assert.Null(port.ReadAgentReceipt(command.Scope with { ProjectId = "other" }, command.IdempotencyKey, command.Fingerprint));
        Assert.Null(port.ReadAgentReceipt(command.Scope with { RoleId = "other" }, command.IdempotencyKey, command.Fingerprint));
        Assert.Null(port.ReadAgentReceipt(command.Scope with { ConversationId = "other" }, command.IdempotencyKey, command.Fingerprint));
        Assert.Null(port.ReadAgentReceipt(command.Scope, command.IdempotencyKey, new string('C', 64)));
        Assert.Single(repo.List());
    }
    [Fact]
    public void Stale_snapshot_and_wrong_role_do_not_write_or_publish_receipt()
    {
        var db = Database();
        var repo = new SqliteTodoRepository(db);
        var original = Item(); repo.Save(original);
        var changed = new TodoItem(original.Id, "changed", null, original.Priority, null, original.CreatedAt, original.UpdatedAt.AddSeconds(1));
        repo.Save(changed);
        var port = (ITodoAgentCommandRepository)repo;
        Assert.Equal(TodoAgentCommitKind.Conflict, port.CommitAgentCommand(Mutation(original, original.Complete(DateTimeOffset.UtcNow))).Kind);
        Assert.Equal(TodoAgentCommitKind.Conflict, port.CommitAgentCommand(Mutation(changed, changed.Complete(DateTimeOffset.UtcNow)) with { Scope = new("conversation", "other-role", null) }).Kind);
        Assert.True(TodoItemValueComparer.Equals(changed, repo.Get(changed.Id)));
        using var connection = db.Open(); using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM todo_agent_receipts";
        Assert.Equal(0L, count.ExecuteScalar());
    }
    [Fact]
    public void Receipt_failure_rolls_back_todo_and_conversation_deletion_cascades_receipt()
    {
        var db = Database(); var repo = new SqliteTodoRepository(db); var port = (ITodoAgentCommandRepository)repo;
        // First call establishes the module schema through a rejected scope, without committing an item.
        port.CommitAgentCommand(Mutation(null, Item()) with { Scope = new("missing", "role", null) });
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON todo_agent_receipts BEGIN SELECT RAISE(ABORT,'fixture'); END;"; command.ExecuteNonQuery(); }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => port.CommitAgentCommand(Mutation(null, Item())));
        Assert.Empty(repo.List());
        using (var connection = db.Open()) using (var command = connection.CreateCommand())
        { command.CommandText = "DROP TRIGGER reject_receipt"; command.ExecuteNonQuery(); }
        Assert.Equal(TodoAgentCommitKind.Committed, port.CommitAgentCommand(Mutation(null, Item())).Kind);
        using (var connection = db.Open()) using (var command = connection.CreateCommand())
        { command.CommandText = "DELETE FROM conversations WHERE conversation_id='conversation'; SELECT COUNT(*) FROM todo_agent_receipts"; Assert.Equal(0L, command.ExecuteScalar()); }
        Assert.Single(repo.List());
    }
    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }
}
