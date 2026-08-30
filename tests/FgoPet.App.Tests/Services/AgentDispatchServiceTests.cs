using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class AgentDispatchServiceTests
{
    [Fact]
    public async Task Accepted_dispatch_activates_todo_and_persists_execution()
    {
        var todos = new FakeTodoRepository();
        var agents = new FakeAgentRepository();
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.High, null);
        var gateway = new FakeGateway(AgentDispatchStatus.Accepted);
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System);

        var result = await service.DispatchAsync(todo, "codex", "project-1", confirmed: true);

        Assert.Equal(AgentDispatchStatus.Accepted, result.Status);
        Assert.Equal(TodoStatus.Active, todos.Get(todo.Id)!.Status);
        Assert.NotNull(agents.SavedExecution);
        Assert.Equal(result.DispatchRequestId, gateway.LastRequest!.DispatchRequestId);
    }

    [Fact]
    public async Task Offline_or_unconfirmed_dispatch_keeps_todo_planned()
    {
        var todos = new FakeTodoRepository();
        var agents = new FakeAgentRepository();
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.Normal, null);
        var gateway = new FakeGateway(AgentDispatchStatus.Offline);
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System);

        var unconfirmed = await service.DispatchAsync(todo, "codex", "project-1", confirmed: false);
        var offline = await service.DispatchAsync(todo, "codex", "project-1", confirmed: true);

        Assert.Equal(AgentDispatchStatus.Failed, unconfirmed.Status);
        Assert.Equal(AgentDispatchStatus.Offline, offline.Status);
        Assert.Equal(TodoStatus.Planned, todos.Get(todo.Id)!.Status);
        Assert.Null(agents.SavedExecution);
    }

    private sealed class FakeGateway(AgentDispatchStatus status) : IAgentGateway
    {
        public AgentDispatchRequest? LastRequest { get; private set; }
        public bool IsConnected => status != AgentDispatchStatus.Offline;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(IsConnected, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentDispatchResult(status, request.DispatchRequestId));
        }
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AgentOpenTaskResult(AgentOpenTaskStatus.Unsupported));
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(IReadOnlyList<AgentExecution> knownExecutions, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        private readonly List<TodoItem> _items = new();
        public void Save(TodoItem todo) { _items.RemoveAll(item => item.Id == todo.Id); _items.Add(todo); }
        public TodoItem? Get(string id) => _items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => status is null ? _items.ToArray() : _items.Where(item => item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) => _items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => _items.Clear();
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public AgentExecution? SavedExecution { get; private set; }
        public void SaveExecution(AgentExecution execution) => SavedExecution = execution;
        public AgentExecution? GetExecution(string id) => SavedExecution?.Id == id ? SavedExecution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => SavedExecution;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => SavedExecution is { IsNonTerminal: true } ? new[] { SavedExecution } : Array.Empty<AgentExecution>();
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }
}
