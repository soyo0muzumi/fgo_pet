using System.Globalization;
using FgoPet.Core.Todo;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

public sealed class SqliteTodoRepository : ITodoRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteTodoRepository(RuntimeDatabase database) => _database = database;

    public bool TryUpdateLocal(TodoItem expected, TodoItem? replacement)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (replacement is not null && replacement.Id != expected.Id)
        {
            throw new ArgumentException("Identity cannot change.");
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        TodoValues currentValues;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = SelectSql + " WHERE todo_id=$id";
            read.Parameters.AddWithValue("$id", expected.Id);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            currentValues = ReadTodoValues(reader);
        }

        var current = CreateTodo(connection, transaction, currentValues);
        if (!TodoItemValueComparer.Equals(current, expected) || current.Status == TodoStatus.Active)
        {
            return false;
        }

        using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = "SELECT COUNT(*) FROM agent_executions WHERE todo_id=$id AND status IN ('dispatching','active','attention','dispatch_outcome_unknown')";
            active.Parameters.AddWithValue("$id", expected.Id);
            if (Convert.ToInt64(active.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                return false;
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (replacement is null)
        {
            command.CommandText = "DELETE FROM todo_items WHERE todo_id=$id";
            command.Parameters.AddWithValue("$id", expected.Id);
        }
        else
        {
            command.CommandText = """
                UPDATE todo_items SET title=$title, description=$description, priority=$priority,
                    due_at_utc=$due, status=$status, created_at_utc=$created, updated_at_utc=$updated,
                    completed_at_utc=$completed WHERE todo_id=$id
                """;
            AddTodoParameters(command, replacement);
        }

        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }

        if (replacement is not null)
        {
            ReplaceSteps(connection, transaction, replacement);
        }

        transaction.Commit();
        return true;
    }

    public void Save(TodoItem todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
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

        ReplaceSteps(connection, transaction, todo);
        transaction.Commit();
    }

    public TodoItem? Get(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE todo_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var values = ReadTodoValues(reader);
        reader.Close();
        return CreateTodo(connection, null, values);
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

        return ReadTodos(connection, null, command);
    }

    public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE status='completed' AND substr(completed_at_utc, 1, 10)=$date ORDER BY completed_at_utc DESC, todo_id";
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return ReadTodos(connection, null, command);
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

        using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = "SELECT COUNT(*) FROM agent_executions WHERE todo_id=$id AND status IN ('dispatching','active','attention','dispatch_outcome_unknown')";
            active.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(active.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException("A Todo with an active or unknown Agent execution cannot be deleted.");
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
        foreach (var table in new[] { "work_archive_items", "work_archives", "long_work_archives", "agent_event_receipts", "agent_executions", "agent_project_snapshots", "todo_steps", "todo_items" })
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

    private static void ReplaceSteps(SqliteConnection connection, SqliteTransaction transaction, TodoItem todo)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM todo_steps WHERE todo_id=$todo_id";
            clear.Parameters.AddWithValue("$todo_id", todo.Id);
            clear.ExecuteNonQuery();
        }

        foreach (var step in todo.Steps)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO todo_steps(step_id, todo_id, title, is_completed, sort_order)
                VALUES($step_id, $todo_id, $title, $is_completed, $sort_order)
                """;
            insert.Parameters.AddWithValue("$step_id", step.Id);
            insert.Parameters.AddWithValue("$todo_id", todo.Id);
            insert.Parameters.AddWithValue("$title", step.Title);
            insert.Parameters.AddWithValue("$is_completed", step.IsCompleted ? 1 : 0);
            insert.Parameters.AddWithValue("$sort_order", step.Order);
            insert.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<TodoItem> ReadTodos(SqliteConnection connection, SqliteTransaction? transaction, SqliteCommand command)
    {
        var values = new List<TodoValues>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                values.Add(ReadTodoValues(reader));
            }
        }

        return values.Select(value => CreateTodo(connection, transaction, value)).ToArray();
    }

    private static TodoItem CreateTodo(SqliteConnection connection, SqliteTransaction? transaction, TodoValues values)
    {
        return new TodoItem(
            values.Id,
            values.Title,
            values.Description,
            values.Priority,
            values.DueAt,
            values.CreatedAt,
            values.UpdatedAt,
            values.Status,
            values.CompletedAt,
            ReadSteps(connection, transaction, values.Id));
    }

    private static IReadOnlyList<TodoStep> ReadSteps(SqliteConnection connection, SqliteTransaction? transaction, string todoId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT step_id, title, is_completed, sort_order FROM todo_steps WHERE todo_id=$todo_id ORDER BY sort_order, step_id";
        command.Parameters.AddWithValue("$todo_id", todoId);
        using var reader = command.ExecuteReader();
        var steps = new List<TodoStep>();
        while (reader.Read())
        {
            steps.Add(new TodoStep(reader.GetString(0), reader.GetString(1), reader.GetInt32(3), reader.GetInt64(2) != 0));
        }

        return steps;
    }

    private static TodoValues ReadTodoValues(SqliteDataReader reader)
    {
        return new TodoValues(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Enum.Parse<TodoPriority>(reader.GetString(3), ignoreCase: true),
            reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
            Enum.Parse<TodoStatus>(reader.GetString(5), ignoreCase: true),
            ParseUtc(reader.GetString(6)),
            ParseUtc(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8)));
    }

    private static string ToDb(TodoStatus status) => status.ToString().ToLowerInvariant();

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record TodoValues(
        string Id,
        string Title,
        string? Description,
        TodoPriority Priority,
        DateTimeOffset? DueAt,
        TodoStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CompletedAt);
}
