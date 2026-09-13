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
