using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class SqliteAgentRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-agent-{Guid.NewGuid():N}.db");

    [Fact]
    public void Event_receipt_is_transactional_and_duplicate_delivery_is_idempotent()
    {
        var database = CreateDatabase();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var todo = new TodoItem("todo-1", "Agent task", null, TodoPriority.Normal, null, at, at);
        todos.Save(todo);
        agents.SaveExecution(new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at));

        var started = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-1");
        var completed = new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.TaskCompleted, at.AddMinutes(2), TodoId: "todo-1");

        Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(started));
        Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(completed));
        Assert.Equal(AgentEventApplyResult.AlreadyApplied, agents.ApplyEvent(completed));
        Assert.Equal(TodoStatus.Completed, Assert.IsType<TodoItem>(todos.Get("todo-1")).Status);
        Assert.Equal(AgentExecutionStatus.Completed, Assert.IsType<AgentExecution>(agents.GetExecution("execution-1")).Status);
    }

    [Fact]
    public void Out_of_order_event_cannot_move_a_terminal_execution_backwards()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        agents.SaveExecution(new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at)
            .MarkCompleted(at.AddMinutes(2)));

        var lateStarted = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-1");

        Assert.Equal(AgentEventApplyResult.IgnoredStale, agents.ApplyEvent(lateStarted));
        Assert.Equal(AgentExecutionStatus.Completed, Assert.IsType<AgentExecution>(agents.GetExecution("execution-1")).Status);
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
