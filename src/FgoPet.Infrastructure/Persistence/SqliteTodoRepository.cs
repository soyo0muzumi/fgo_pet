using System.Globalization;
using FgoPet.Core.Todo;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

public sealed class SqliteTodoRepository : ITodoRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteTodoRepository(RuntimeDatabase database) => _database = database;

    public void Save(TodoItem todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO todo_items(todo_id, title, description, priority, due_at_utc, status,
                                   created_at_utc, updated_at_utc, completed_at_utc)
            VALUES($id, $title, $description, $priority, $due, $status, $created, $updated, $completed)
            ON CONFLICT(todo_id) DO UPDATE SET
              title=excluded.title,
              description=excluded.description,
              priority=excluded.priority,
              due_at_utc=excluded.due_at_utc,
              status=excluded.status,
              created_at_utc=excluded.created_at_utc,
              updated_at_utc=excluded.updated_at_utc,
              completed_at_utc=excluded.completed_at_utc
            """;
        AddTodoParameters(command, todo);
        command.ExecuteNonQuery();
    }

    public TodoItem? Get(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE todo_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTodo(reader) : null;
    }

    public IReadOnlyList<TodoItem> List(TodoStatus? status = null)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + (status is null ? string.Empty : " WHERE status=$status")
            + " ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'planned' THEN 1 ELSE 2 END, updated_at_utc DESC, todo_id";
        if (status is not null)
        {
            command.Parameters.AddWithValue("$status", ToDb(status.Value));
        }

        return ReadTodos(command);
    }

    public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE status='completed' AND substr(completed_at_utc, 1, 10)=$date ORDER BY completed_at_utc DESC, todo_id";
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return ReadTodos(command);
    }

    public void Delete(string id)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT status FROM todo_items WHERE todo_id=$id";
            read.Parameters.AddWithValue("$id", id);
            var status = read.ExecuteScalar() as string;
            if (string.Equals(status, "active", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An active Todo must be cancelled by its Agent before deletion.");
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM todo_items WHERE todo_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ClearAgentTodoData()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[] { "work_archive_items", "work_archives", "long_work_archives", "agent_event_receipts", "agent_executions", "todo_items" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table}";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal static void UpdateStatus(SqliteConnection connection, SqliteTransaction transaction, string todoId, TodoStatus status, DateTimeOffset at)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE todo_items
            SET status=$status,
                updated_at_utc=$updated,
                completed_at_utc=$completed
            WHERE todo_id=$id
            """;
        command.Parameters.AddWithValue("$status", ToDb(status));
        command.Parameters.AddWithValue("$updated", at.ToString("O"));
        command.Parameters.AddWithValue("$completed", status == TodoStatus.Completed ? at.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$id", todoId);
        command.ExecuteNonQuery();
    }

    private const string SelectSql = "SELECT todo_id, title, description, priority, due_at_utc, status, created_at_utc, updated_at_utc, completed_at_utc FROM todo_items";

    private static void AddTodoParameters(SqliteCommand command, TodoItem todo)
    {
        command.Parameters.AddWithValue("$id", todo.Id);
        command.Parameters.AddWithValue("$title", todo.Title);
        command.Parameters.AddWithValue("$description", todo.Description ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$priority", todo.Priority.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$due", todo.DueAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", ToDb(todo.Status));
        command.Parameters.AddWithValue("$created", todo.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", todo.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed", todo.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static IReadOnlyList<TodoItem> ReadTodos(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<TodoItem>();
        while (reader.Read()) result.Add(ReadTodo(reader));
        return result;
    }

    private static TodoItem ReadTodo(SqliteDataReader reader)
    {
        return new TodoItem(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<TodoPriority>(reader.GetString(3), ignoreCase: true),
            reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
            ParseUtc(reader.GetString(6)),
            ParseUtc(reader.GetString(7)),
            Enum.Parse<TodoStatus>(reader.GetString(5), ignoreCase: true),
            reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8)));
    }

    private static string ToDb(TodoStatus status) => status.ToString().ToLowerInvariant();

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
