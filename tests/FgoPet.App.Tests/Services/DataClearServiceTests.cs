using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class DataClearServiceTests
{
    [Fact]
    public void Clear_agent_todo_data_delegates_to_the_agent_data_boundary()
    {
        var repository = new FakeTodoRepository();
        repository.HasData = true;
        var service = new DataClearService(repository);

        service.ClearAgentTodoData();

        Assert.False(repository.HasData);
    }

    [Fact]
    public void Clear_agent_todo_data_also_requests_relay_pending_data_clear()
    {
        var relay = new FakeGateway();
        var service = new DataClearService(new FakeTodoRepository { HasData = true }, relay);

        service.ClearAgentTodoData();

        Assert.True(relay.ClearRequested);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public bool HasData { get; set; }
        public void Save(TodoItem todo) => HasData = true;
        public TodoItem? Get(string id) => null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Array.Empty<TodoItem>();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) { }
        public void ClearAgentTodoData() => HasData = false;
    }

    private sealed class FakeGateway : IAgentGateway
    {
        public bool ClearRequested { get; private set; }
        public bool IsConnected => true;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(true, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(IReadOnlyList<AgentExecution> knownExecutions, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public Task ClearPendingEventsAsync(CancellationToken cancellationToken = default)
        {
            ClearRequested = true;
            return Task.CompletedTask;
        }
    }
}
