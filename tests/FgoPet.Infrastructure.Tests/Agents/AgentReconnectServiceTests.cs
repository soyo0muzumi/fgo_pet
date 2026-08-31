using FgoPet.Core.Agents;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRuntime;
using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Agents;

public sealed class AgentReconnectServiceTests
{
    [Fact]
    public async Task Poll_keeps_delivery_pending_until_projection_is_persisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fgo-reconnect-ack-{Guid.NewGuid():N}.db");
        try
        {
            var database = new RuntimeDatabase(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var todos = new SqliteTodoRepository(database);
            var agents = new SqliteAgentRepository(database);
            var at = DateTimeOffset.UtcNow;
            todos.Save(new TodoItem("todo-ack", "Delivery", null, TodoPriority.Normal, null, at, at));
            agents.SaveExecution(new AgentExecution("execution-ack", "todo-ack", "codex", "source-1", "task-ack", "dispatch-ack", at));
            var pending = true;
            var acknowledgements = 0;
            var envelope = ProtocolEnvelope.Create("event-ack", "agent_event",
                new AgentEventMessage("codex", "source-1", "task-ack", 1, "task_started", at));
            var gateway = new AgentRelayClient(new AgentControlClient((request, _) =>
            {
                if (request.MessageType == "event_ack")
                {
                    Assert.Equal(TodoStatus.Active, todos.Get("todo-ack")!.Status);
                    acknowledgements++;
                    pending = false;
                    return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, request.MessageType,
                        new { result = "acknowledged" }).ToJson());
                }
                return Task.FromResult(ProtocolEnvelope.Create(request.MessageId, request.MessageType,
                    new { result = "status", events = pending ? new[] { envelope } : Array.Empty<ProtocolEnvelope>() }).ToJson());
            }));
            var failProjection = true;
            var service = new AgentReconnectService(gateway, agents, new AgentEventProjector(agents), action =>
            {
                if (failProjection) throw new IOException("Projection unavailable");
                action();
                return Task.CompletedTask;
            });

            Assert.Equal(0, await service.PollAsync());
            Assert.True(pending);
            Assert.Equal(0, acknowledgements);
            failProjection = false;
            Assert.Equal(1, await service.PollAsync());
            Assert.False(pending);
            Assert.Equal(1, acknowledgements);
            Assert.Equal(0, await service.PollAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task Reconnect_queries_only_known_non_terminal_executions()
    {
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var execution = new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at);
        var gateway = new FakeGateway(new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at));
        var repository = new FakeAgentRepository(execution);
        var service = new AgentReconnectService(gateway, repository, new AgentEventProjector());

        var result = await service.ReconnectAsync();

        Assert.True(result.Connected);
        Assert.Equal(1, result.KnownExecutionCount);
        Assert.Equal(1, gateway.QueriedCount);
        Assert.Single(gateway.LastKnownExecutions);
    }

    [Fact]
    public async Task Reconnect_rehydrates_a_persisted_active_execution_when_relay_has_no_new_event()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fgo-reconnect-{Guid.NewGuid():N}.db");
        try
        {
            var database = new RuntimeDatabase(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var todos = new SqliteTodoRepository(database);
            var agents = new SqliteAgentRepository(database);
            var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
            todos.Save(new TodoItem("todo-restart", "Resume", null, TodoPriority.Normal, null, at, at));
            agents.SaveExecution(new AgentExecution(
                "execution-restart", "todo-restart", "codex", "source-1", "task-restart", "dispatch-restart", at));
            Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(new AgentEvent(
                "codex", "source-1", "task-restart", 1, AgentEventType.TaskStarted, at.AddMinutes(1),
                TodoId: "todo-restart")));

            // A fresh projector models a new App process. The Relay can report
            // no new receipt after the previous process already persisted it.
            var projector = new AgentEventProjector(agents);
            var service = new AgentReconnectService(new EmptyGateway(), agents, projector);
            var result = await service.ReconnectAsync();

            Assert.True(result.Connected);
            Assert.Equal(AgentExecutionStatus.Active, Assert.Single(projector.Current).Status);
            Assert.Equal("task-restart", projector.Current.Single().TaskId);
            Assert.Equal(TodoStatus.Active, todos.Get("todo-restart")!.Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    private sealed class FakeGateway(AgentEvent agentEvent) : IAgentGateway
    {
        public int QueriedCount { get; private set; }
        public IReadOnlyList<AgentExecution> LastKnownExecutions { get; private set; } = Array.Empty<AgentExecution>();
        public bool IsConnected => true;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(true, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(IReadOnlyList<AgentExecution> knownExecutions, CancellationToken cancellationToken = default)
        {
            QueriedCount++;
            LastKnownExecutions = knownExecutions;
            return Task.FromResult<IReadOnlyList<AgentEvent>>(new[] { agentEvent });
        }
    }

    private sealed class EmptyGateway : IAgentGateway
    {
        public bool IsConnected => true;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentGatewayStatus(true, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(
            IReadOnlyList<AgentExecution> knownExecutions,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class FakeAgentRepository(AgentExecution execution) : IAgentRepository
    {
        public void SaveExecution(AgentExecution execution) => throw new NotSupportedException();
        public AgentExecution? GetExecution(string id) => execution.Id == id ? execution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => execution;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => new[] { execution };
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) => throw new NotSupportedException();
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }
}
