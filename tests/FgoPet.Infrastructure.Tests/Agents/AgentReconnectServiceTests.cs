using FgoPet.Core.Agents;
using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Agents;

public sealed class AgentReconnectServiceTests
{
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
