using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

public sealed partial class SqliteTodoRepository
{
    private readonly object nativeSchemaGate = new();
    private bool nativeSchemaReady;
    public TodoAgentCommitResult? ReadAgentReceipt(ToolScope scope, string idempotencyKey, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureNativeSchema();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.todo_id,r.result_version FROM todo_agent_receipts r
            JOIN conversations c ON c.conversation_id=r.conversation_id
            WHERE r.idempotency_key=$key AND r.fingerprint=$fingerprint
              AND r.conversation_id=$conversation AND r.role_id=$role AND r.project_id IS $project
              AND c.servant_id=$role AND c.project_id IS $project AND c.status='active'
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$conversation", scope.ConversationId);
        command.Parameters.AddWithValue("$role", scope.RoleId);
        command.Parameters.AddWithValue("$project", (object?)scope.ProjectId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(TodoAgentCommitKind.AlreadyCommitted, reader.GetString(0), reader.GetString(1)) : null;
    }
    private void EnsureNativeSchema()
    {
        lock (nativeSchemaGate)
        {
            if (nativeSchemaReady) return;
            new RuntimeDatabaseMigrator(_database).MigrateModule("todo-agent", [new(1, """
                CREATE TABLE todo_agent_receipts(
                  idempotency_key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL,
                  conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id) ON DELETE CASCADE,
                  role_id TEXT NOT NULL, project_id TEXT NULL, todo_id TEXT NOT NULL,
                  result_version TEXT NOT NULL, committed_at_utc TEXT NOT NULL);
                CREATE INDEX ix_todo_agent_receipts_conversation ON todo_agent_receipts(conversation_id);
                """)]);
            nativeSchemaReady = true;
        }
    }
    public TodoAgentCommitResult CommitAgentCommand(TodoAgentCommit mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        static bool Digest(string value) => value is { Length: 64 } && value.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9');
        if (mutation.Scope is null || !Digest(mutation.IdempotencyKey) || !Digest(mutation.Fingerprint) ||
            mutation.Replacement is null || mutation.Expected is not null && mutation.Expected.Id != mutation.Replacement.Id)
            throw new ArgumentException("Invalid Todo agent commit.");
        EnsureNativeSchema();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var scope = connection.CreateCommand())
        {
            scope.Transaction = transaction;
            scope.CommandText = "SELECT COUNT(*) FROM conversations WHERE conversation_id=$conversation AND servant_id=$role AND status='active' AND project_id IS $project";
            scope.Parameters.AddWithValue("$conversation", mutation.Scope.ConversationId);
            scope.Parameters.AddWithValue("$role", mutation.Scope.RoleId);
            scope.Parameters.AddWithValue("$project", (object?)mutation.Scope.ProjectId ?? DBNull.Value);
            if ((long)scope.ExecuteScalar()! != 1) return new(TodoAgentCommitKind.Conflict);
        }
        using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = "SELECT fingerprint,conversation_id,role_id,project_id,todo_id,result_version FROM todo_agent_receipts WHERE idempotency_key=$key";
            receipt.Parameters.AddWithValue("$key", mutation.IdempotencyKey);
            using var reader = receipt.ExecuteReader();
            if (reader.Read()) return reader.GetString(0) == mutation.Fingerprint && reader.GetString(1) == mutation.Scope.ConversationId &&
                reader.GetString(2) == mutation.Scope.RoleId && (reader.IsDBNull(3) ? null : reader.GetString(3)) == mutation.Scope.ProjectId &&
                reader.GetString(4) == mutation.Replacement.Id
                    ? new(TodoAgentCommitKind.AlreadyCommitted, reader.GetString(4), reader.GetString(5))
                    : new(TodoAgentCommitKind.Conflict);
        }
        TodoItem? current = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = SelectSql + " WHERE todo_id=$id";
            read.Parameters.AddWithValue("$id", mutation.Replacement.Id);
            TodoValues? values;
            using (var reader = read.ExecuteReader()) values = reader.Read() ? ReadTodoValues(reader) : null;
            if (values is not null) current = CreateTodo(connection, transaction, values);
        }
        if (!TodoItemValueComparer.Equals(current, mutation.Expected) || current?.Status == TodoStatus.Active ||
            mutation.Expected is null && mutation.Replacement.Status != TodoStatus.Planned ||
            current?.Status == TodoStatus.Completed)
            return new(TodoAgentCommitKind.Conflict);
        using (var quota = connection.CreateCommand())
        {
            quota.Transaction = transaction;
            quota.CommandText = "SELECT COUNT(*) FROM todo_agent_receipts";
            if ((long)quota.ExecuteScalar()! >= 4096) return new(TodoAgentCommitKind.Unavailable);
        }
        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = mutation.Expected is null ? """
                INSERT INTO todo_items(todo_id,title,description,priority,due_at_utc,status,created_at_utc,updated_at_utc,completed_at_utc)
                VALUES($id,$title,$description,$priority,$due,$status,$created,$updated,$completed)
                """ : """
                UPDATE todo_items SET title=$title,description=$description,priority=$priority,due_at_utc=$due,
                  status=$status,created_at_utc=$created,updated_at_utc=$updated,completed_at_utc=$completed WHERE todo_id=$id
                """;
            AddTodoParameters(write, mutation.Replacement);
            if (write.ExecuteNonQuery() != 1) return new(TodoAgentCommitKind.Conflict);
            ReplaceSteps(connection, transaction, mutation.Replacement);
        }
        var version = TodoItemVersion.Of(mutation.Replacement);
        using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO todo_agent_receipts(idempotency_key,fingerprint,conversation_id,role_id,project_id,todo_id,result_version,committed_at_utc)
                VALUES($key,$fingerprint,$conversation,$role,$project,$todo,$version,$at)
                """;
            receipt.Parameters.AddWithValue("$key", mutation.IdempotencyKey);
            receipt.Parameters.AddWithValue("$fingerprint", mutation.Fingerprint);
            receipt.Parameters.AddWithValue("$conversation", mutation.Scope.ConversationId);
            receipt.Parameters.AddWithValue("$role", mutation.Scope.RoleId);
            receipt.Parameters.AddWithValue("$project", (object?)mutation.Scope.ProjectId ?? DBNull.Value);
            receipt.Parameters.AddWithValue("$todo", mutation.Replacement.Id);
            receipt.Parameters.AddWithValue("$version", version);
            receipt.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            receipt.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(TodoAgentCommitKind.Committed, mutation.Replacement.Id, version);
    }
}
