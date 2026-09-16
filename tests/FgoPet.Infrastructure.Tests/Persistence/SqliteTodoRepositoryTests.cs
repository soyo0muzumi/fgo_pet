using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class SqliteTodoRepositoryTests : IDisposable
{
    [Fact]
    public void Local_update_rejects_stale_snapshot_and_nonterminal_execution()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.UtcNow;
        var todo = new TodoItem("local-1", "Learn", null, TodoPriority.Normal, null, at, at);
        repository.Save(todo);
        var edited = new TodoItem(todo.Id, "Learn attention", todo.Description, todo.Priority, todo.DueAt, todo.CreatedAt, at.AddSeconds(1));
        Assert.True(repository.TryUpdateLocal(todo, edited));
        Assert.False(repository.TryUpdateLocal(todo, todo.Complete(at.AddSeconds(2))));
        agents.SaveExecution(new FgoPet.Core.Agents.AgentExecution("exec", todo.Id, "codex", "instance", "task", "request", at,
            FgoPet.Core.Agents.AgentExecutionStatus.DispatchOutcomeUnknown));
        Assert.False(repository.TryUpdateLocal(edited, edited.Complete(at.AddSeconds(3))));
        Assert.False(repository.TryUpdateLocal(edited, null));
        Assert.Equal(edited, repository.Get(todo.Id));
        Assert.True(agents.TryResumeUnknown("exec", at.AddSeconds(4)));
        Assert.False(agents.TryResumeUnknown("exec", at.AddSeconds(5)));
        Assert.Equal(0, agents.GetLatestEventSequence("codex", "instance", "task"));
        Assert.Equal(FgoPet.Core.Agents.AgentEventApplyResult.Applied, agents.ApplyEvent(
            new FgoPet.Core.Agents.AgentEvent("codex", "instance", "task", 1,
                FgoPet.Core.Agents.AgentEventType.TaskCompleted, at.AddSeconds(6), TodoId: todo.Id, DispatchRequestId: "request")));
        Assert.Equal(TodoStatus.Completed, repository.Get(todo.Id)!.Status);
    }
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-todo-{Guid.NewGuid():N}.db");

    [Fact]
    public void Todo_items_round_trip_and_filter_by_local_completion_date()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var todo = new TodoItem(
            "todo-1", "Ship it", "Description", TodoPriority.High, null,
            DateTimeOffset.Parse("2026-08-30T08:00:00+08:00"),
            DateTimeOffset.Parse("2026-08-30T08:00:00+08:00"));

        repository.Save(todo);
        var active = todo.Activate(todo.CreatedAt.AddMinutes(1));
        repository.Save(active);
        repository.Save(active.Complete(active.UpdatedAt.AddMinutes(1)));

        var loaded = Assert.IsType<TodoItem>(repository.Get("todo-1"));
        Assert.Equal(TodoStatus.Completed, loaded.Status);
        Assert.Single(repository.ListCompletedOn(DateOnly.Parse("2026-08-30")));
    }

    [Fact]
    public void Todo_steps_round_trip_and_parent_edits_preserve_an_unedited_checklist()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var at = DateTimeOffset.Parse("2026-09-13T08:00:00Z");
        var todo = new TodoItem("todo-steps", "Ship it", "Keep this", TodoPriority.High, null, at, at,
            steps: new[]
            {
                new TodoStep("step-1", "Prepare", 0),
                new TodoStep("step-2", "Check", 1, isCompleted: true),
            });

        repository.Save(todo);
        var loaded = Assert.IsType<TodoItem>(repository.Get(todo.Id));
        Assert.True(TodoItemValueComparer.Equals(todo, loaded));

        var edited = new TodoItem(loaded.Id, "Ship now", loaded.Description, loaded.Priority, loaded.DueAt,
            loaded.CreatedAt, at.AddMinutes(1), loaded.Status, loaded.CompletedAt, loaded.Steps);
        repository.Save(edited);

        var afterEdit = Assert.IsType<TodoItem>(repository.Get(todo.Id));
        Assert.Equal("Ship now", afterEdit.Title);
        Assert.Equal(todo.Steps[0], afterEdit.Steps[0]);
        Assert.True(afterEdit.Steps[1].IsCompleted);
    }

    [Fact]
    public void Local_update_compares_step_values_and_rejects_a_stale_checklist_snapshot()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var at = DateTimeOffset.UtcNow;
        var todo = new TodoItem("todo-stale-steps", "Learn", null, TodoPriority.Normal, null, at, at,
            steps: new[] { new TodoStep("step-1", "Prepare", 0) });
        repository.Save(todo);
        var changed = todo.WithSteps(new[] { new TodoStep("step-1", "Changed", 0) });
        repository.Save(changed);

        Assert.False(repository.TryUpdateLocal(todo, todo.Complete(at.AddMinutes(1))));
        Assert.True(TodoItemValueComparer.Equals(changed, repository.Get(todo.Id)));
    }

    [Fact]
    public void Completed_todo_can_be_reopened_and_a_stale_reopen_cannot_overwrite_a_newer_update()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var createdAt = DateTimeOffset.Parse("2026-09-13T01:02:03Z");
        var todo = new TodoItem("todo-reopen", "Restore me", "Keep this", TodoPriority.High,
            DateTimeOffset.Parse("2026-09-20T08:00:00+08:00"), createdAt, createdAt);
        var completed = todo.Complete(createdAt.AddMinutes(1));
        repository.Save(completed);
        var reopened = completed with
        {
            Status = TodoStatus.Planned,
            CompletedAt = null,
            UpdatedAt = createdAt.AddMinutes(2),
        };

        Assert.True(repository.TryUpdateLocal(completed, reopened));
        Assert.Equal(reopened, repository.Get(todo.Id));
        Assert.Empty(repository.List(TodoStatus.Completed));
        Assert.Single(repository.List(TodoStatus.Planned));

        var newerCompleted = new TodoItem(todo.Id, "Changed elsewhere", todo.Description, todo.Priority, todo.DueAt,
            todo.CreatedAt, createdAt.AddMinutes(3), TodoStatus.Completed, createdAt.AddMinutes(3));
        repository.Save(newerCompleted);
        Assert.False(repository.TryUpdateLocal(reopened, reopened with { Status = TodoStatus.Planned, UpdatedAt = createdAt.AddMinutes(4) }));
        Assert.Equal(newerCompleted, repository.Get(todo.Id));
    }

    [Fact]
    public void Delete_rejects_active_todos_but_allows_planned_todos()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var todo = new TodoItem("todo-1", "Delete me", null, TodoPriority.Normal, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        repository.Save(todo);
        repository.Delete("todo-1");
        Assert.Null(repository.Get("todo-1"));

        var active = new TodoItem("todo-2", "Keep me", null, TodoPriority.Normal, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            .Activate(DateTimeOffset.UtcNow.AddMinutes(1));
        repository.Save(active);

        Assert.Throws<InvalidOperationException>(() => repository.Delete("todo-2"));
    }

    [Fact]
    public void Delete_cascades_steps_and_rejects_unknown_execution()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.UtcNow;
        var todo = new TodoItem("todo-delete-steps", "Delete me", null, TodoPriority.Normal, null, at, at,
            steps: new[] { new TodoStep("step-1", "Prepare", 0) });
        repository.Save(todo);
        repository.Delete(todo.Id);

        using (var connection = database.Open())
        {
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM todo_steps";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }

        repository.Save(todo);
        agents.SaveExecution(new FgoPet.Core.Agents.AgentExecution("unknown-delete", todo.Id, "codex", "instance", "task", "request", at,
            FgoPet.Core.Agents.AgentExecutionStatus.DispatchOutcomeUnknown));
        Assert.Throws<InvalidOperationException>(() => repository.Delete(todo.Id));
        Assert.NotNull(repository.Get(todo.Id));
    }

    [Fact]
    public void Clear_agent_todo_data_removes_steps_with_parent_rows()
    {
        var database = CreateDatabase();
        var repository = new SqliteTodoRepository(database);
        var at = DateTimeOffset.UtcNow;
        repository.Save(new TodoItem("todo-clear-steps", "Clear me", null, TodoPriority.Normal, null, at, at,
            steps: new[] { new TodoStep("step-1", "Prepare", 0) }));

        repository.ClearAgentTodoData();

        Assert.Null(repository.Get("todo-clear-steps"));
        using var connection = database.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM todo_steps";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
    }
}
