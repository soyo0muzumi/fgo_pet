using FgoPet.AgentProtocol;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Agents;

public sealed class AgentRelayAdministrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-agent-revoke-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Successful_revoke_cancels_matching_source_instance_and_restores_its_todo()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-31T08:00:00Z");
        var matchingTodo = new TodoItem("todo-match", "Matching", null, TodoPriority.Normal, null, at, at);
        var otherInstanceTodo = new TodoItem("todo-other-instance", "Other instance", null, TodoPriority.Normal, null, at, at);
        var otherSourceTodo = new TodoItem("todo-other-source", "Other source", null, TodoPriority.Normal, null, at, at);
        todos.Save(matchingTodo);
        todos.Save(otherInstanceTodo);
        todos.Save(otherSourceTodo);
        agents.SaveExecution(new AgentExecution("execution-match", "todo-match", "codex", "instance-1", "task-1", "dispatch-1", at));
        agents.SaveExecution(new AgentExecution("execution-other-instance", "todo-other-instance", "codex", "instance-2", "task-2", "dispatch-2", at));
        agents.SaveExecution(new AgentExecution("execution-other-source", "todo-other-source", "cursor", "instance-1", "task-3", "dispatch-3", at));
        agents.ApplyEvent(new AgentEvent("codex", "instance-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-match"));
        agents.ApplyEvent(new AgentEvent("codex", "instance-2", "task-2", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-other-instance"));
        agents.ApplyEvent(new AgentEvent("cursor", "instance-1", "task-3", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-other-source"));

        var projector = new AgentEventProjector(agents);
        var requests = new List<ProtocolEnvelope>();
        var control = new AgentControlClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, request.MessageType, new { result = "ok" }).ToJson());
        });
        var administration = new AgentRelayAdministration(control, agents, projector);

        await administration.RevokeSourceAsync("codex", "instance-1");

        Assert.Equal(TodoStatus.Planned, todos.Get("todo-match")!.Status);
        Assert.Equal(TodoStatus.Active, todos.Get("todo-other-instance")!.Status);
        Assert.Equal(TodoStatus.Active, todos.Get("todo-other-source")!.Status);
        Assert.Equal(AgentExecutionStatus.Cancelled, agents.GetExecution("codex", "instance-1", "task-1")!.Status);
        Assert.Equal(AgentExecutionStatus.Active, agents.GetExecution("codex", "instance-2", "task-2")!.Status);
        Assert.Equal(AgentExecutionStatus.Active, agents.GetExecution("cursor", "instance-1", "task-3")!.Status);
        Assert.Equal(AgentExecutionStatus.Cancelled, projector.Get("codex/instance-1/task-1")!.Status);
        Assert.Equal("codex", requests.Single().Payload.GetProperty("source_type").GetString());
        Assert.Equal("instance-1", requests.Single().Payload.GetProperty("source_instance_id").GetString());
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
}
