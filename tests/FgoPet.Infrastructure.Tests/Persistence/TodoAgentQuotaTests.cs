using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class TodoAgentQuotaTests
{
    [Fact]
    public void Receipt_quota_allows_4095_and_4096_then_rejects_4097_without_eviction_or_partial_effects()
    {
        using var fixture = new Fixture();
        fixture.SeedReceipts(4094);
        var repository = fixture.Repository;
        var port = (ITodoAgentCommandRepository)repository;
        var scope = new ToolScope("conversation", "role", null);
        var at = DateTimeOffset.UnixEpoch;
        var first = Mutation(scope, "live-4095", "first fingerprint", new TodoItem(
            "todo-4095", "synthetic 4095", null, TodoPriority.Normal, null, at, at,
            steps: [new TodoStep("step-4095", "synthetic step", 0)]));
        var second = Mutation(scope, "live-4096", "second fingerprint", new TodoItem(
            "todo-4096", "synthetic 4096", null, TodoPriority.Normal, null, at, at,
            steps: [new TodoStep("step-4096", "synthetic step", 0)]));
        var rejected = Mutation(scope, "live-4097", "third fingerprint", new TodoItem(
            "todo-4097", "synthetic 4097", null, TodoPriority.Normal, null, at, at,
            steps: [new TodoStep("step-4097", "must not persist", 0)]));

        Assert.Equal(TodoAgentCommitKind.Committed, port.CommitAgentCommand(first).Kind);
        Assert.Equal(4095L, fixture.ReceiptCount());
        Assert.Equal(TodoAgentCommitKind.Committed, port.CommitAgentCommand(second).Kind);
        Assert.Equal(4096L, fixture.ReceiptCount());

        var retryAtCapacity = port.CommitAgentCommand(second);
        Assert.Equal(TodoAgentCommitKind.AlreadyCommitted, retryAtCapacity.Kind);
        Assert.Equal(second.Replacement.Id, retryAtCapacity.ItemId);
        Assert.Equal(TodoItemVersion.Of(second.Replacement), retryAtCapacity.Version);

        Assert.Equal(TodoAgentCommitKind.Unavailable, port.CommitAgentCommand(rejected).Kind);
        Assert.Equal(4096L, fixture.ReceiptCount());
        Assert.Equal(2, repository.List().Count);
        Assert.Null(repository.Get(rejected.Replacement.Id));
        Assert.Equal(0L, fixture.StepCount(rejected.Replacement.Id));
        Assert.True(fixture.ReceiptExists(Fixture.SeededKey(1), Fixture.SeededFingerprint(1)));
        Assert.True(fixture.ReceiptExists(second.IdempotencyKey, second.Fingerprint));
    }

    [Fact]
    public void Receipt_replay_rejects_other_conversation_project_and_changed_parameters()
    {
        using var fixture = new Fixture();
        fixture.AddConversation("project-conversation", "role", "project-a");
        var port = (ITodoAgentCommandRepository)fixture.Repository;
        var at = DateTimeOffset.UnixEpoch;
        var command = Mutation(new("project-conversation", "role", "project-a"), "project-bound", "original arguments",
            new TodoItem("todo-project", "original", null, TodoPriority.Normal, null, at, at));

        Assert.Equal(TodoAgentCommitKind.Committed, port.CommitAgentCommand(command).Kind);
        Assert.Equal(TodoAgentCommitKind.Conflict, port.CommitAgentCommand(command with
        {
            Scope = new("other-conversation", "role", "project-a")
        }).Kind);
        Assert.Equal(TodoAgentCommitKind.Conflict, port.CommitAgentCommand(command with
        {
            Scope = new("project-conversation", "role", "project-b")
        }).Kind);

        var changedItem = new TodoItem(command.Replacement.Id, "changed arguments", null, TodoPriority.Normal, null, at, at);
        Assert.Equal(TodoAgentCommitKind.Conflict, port.CommitAgentCommand(command with
        {
            Fingerprint = Fixture.Digest("changed arguments"),
            Replacement = changedItem
        }).Kind);

        Assert.Equal("original", fixture.Repository.Get(command.Replacement.Id)!.Title);
        Assert.Equal(1L, fixture.ReceiptCount());
    }

    private static TodoAgentCommit Mutation(ToolScope scope, string key, string fingerprint, TodoItem replacement) =>
        new(scope, Fixture.Digest(key), Fixture.Digest(fingerprint), null, replacement);

    private sealed class Fixture : IDisposable
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), "fgopet-todo-quota-" + Guid.NewGuid().ToString("N") + ".db");

        public Fixture()
        {
            Database = new RuntimeDatabase(databasePath, pooling: false);
            new RuntimeDatabaseMigrator(Database).Migrate();
            AddConversation("conversation", "role", null);
            Repository = new SqliteTodoRepository(Database);

            // Initialize only the module-owned receipt schema in this synthetic database.
            var probe = Mutation(new("missing-conversation", "role", null), "schema-probe", "schema-probe",
                new TodoItem("schema-probe", "synthetic", null, TodoPriority.Normal, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
            Assert.Equal(TodoAgentCommitKind.Conflict, ((ITodoAgentCommandRepository)Repository).CommitAgentCommand(probe).Kind);
        }

        public RuntimeDatabase Database { get; }
        public SqliteTodoRepository Repository { get; }

        public void AddConversation(string conversationId, string roleId, string? projectId)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversations(conversation_id,servant_id,created_at_utc,updated_at_utc,status,project_id)
                VALUES($conversation,$role,'2026-10-06','2026-10-06','active',$project)
                """;
            command.Parameters.AddWithValue("$conversation", conversationId);
            command.Parameters.AddWithValue("$role", roleId);
            command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        public void SeedReceipts(int count)
        {
            using var connection = Database.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO todo_agent_receipts(idempotency_key,fingerprint,conversation_id,role_id,project_id,todo_id,result_version,committed_at_utc)
                VALUES($key,$fingerprint,'conversation','role',NULL,$todo,$version,'2026-10-06T00:00:00.0000000+00:00')
                """;
            var key = command.Parameters.Add("$key", SqliteType.Text);
            var fingerprint = command.Parameters.Add("$fingerprint", SqliteType.Text);
            var todo = command.Parameters.Add("$todo", SqliteType.Text);
            var version = command.Parameters.Add("$version", SqliteType.Text);

            for (var index = 1; index <= count; index++)
            {
                key.Value = SeededKey(index);
                fingerprint.Value = SeededFingerprint(index);
                todo.Value = "synthetic-todo-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                version.Value = Digest("seed-version-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public long ReceiptCount() => Scalar("SELECT COUNT(*) FROM todo_agent_receipts");

        public long StepCount(string todoId)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM todo_steps WHERE todo_id=$id";
            command.Parameters.AddWithValue("$id", todoId);
            return (long)command.ExecuteScalar()!;
        }

        public bool ReceiptExists(string key, string fingerprint)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM todo_agent_receipts WHERE idempotency_key=$key AND fingerprint=$fingerprint";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            return (long)command.ExecuteScalar()! == 1;
        }

        private long Scalar(string sql)
        {
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }

        public static string SeededKey(int index) => Digest("seed-key-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static string SeededFingerprint(int index) => Digest("seed-fingerprint-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var exactPath = databasePath + suffix;
                if (File.Exists(exactPath)) File.Delete(exactPath);
            }
        }
    }
}
