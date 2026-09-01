using System.IO;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Agents;
using FgoPet.App.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class AgentDispatchServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-dispatch-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(1, null, AgentDispatchStatus.Accepted)]
    [InlineData(2, null, AgentDispatchStatus.Failed)]
    [InlineData(2, "instance-2", AgentDispatchStatus.Accepted)]
    public async Task Production_dispatch_uses_one_authorized_instance_or_requires_explicit_selection(int count, string? instance, AgentDispatchStatus expected)
    {
        var todos = new FakeTodoRepository();
        var agents = new FakeAgentRepository();
        var gateway = new FakeGateway(AgentDispatchStatus.Accepted);
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.Normal, null);
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System, new FakeAdministration(count));
        var result = await service.DispatchAsync(todo, "codex", "project-1", true, sourceInstanceId: instance);
        Assert.Equal(expected, result.Status);
        if (expected == AgentDispatchStatus.Accepted)
        {
            Assert.Equal(instance ?? "instance-1", gateway.LastRequest!.SourceInstanceId);
            Assert.Equal(gateway.LastRequest.SourceInstanceId, agents.SavedExecution!.SourceInstance);
        }
        else Assert.Null(gateway.LastRequest);
    }

    private sealed class FakeAdministration(int count) : IAgentRelayAdministration
    {
        public Task<AgentRelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(
            new AgentRelaySnapshot(AgentRelayConnectionState.Connected, true, true, true, DateTimeOffset.UtcNow, [],
                Enumerable.Range(1, count).Select(i => new AgentApprovedSource("codex", $"instance-{i}", "Codex", "1", true, ["project-1"], true)).ToArray()));
        public Task<AgentRelaySnapshot> TestConnectionAsync(CancellationToken cancellationToken = default) => GetSnapshotAsync(cancellationToken);
        public Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdatePermissionsAsync(string sourceType, string sourceInstanceId, IReadOnlyList<string> targetIds, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeSourceAsync(string sourceType, string sourceInstanceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

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
    public async Task Sqlite_dispatch_ack_cannot_reactivate_a_todo_completed_before_ack()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.High, null);
        var gateway = new FakeGateway(AgentDispatchStatus.Accepted, request =>
        {
            var at = DateTimeOffset.UtcNow;
            Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(new AgentEvent(
                "codex", "relay", request.DispatchRequestId, 1, AgentEventType.TaskCompleted, at,
                summary: "Delivered", TodoId: request.TodoId, DispatchRequestId: request.DispatchRequestId)));
            return Task.CompletedTask;
        });
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System);

        var result = await service.DispatchAsync(todo, "codex", "project-1", confirmed: true);

        Assert.Equal(AgentDispatchStatus.Accepted, result.Status);
        Assert.Equal(TodoStatus.Completed, todos.Get(todo.Id)!.Status);
        Assert.Equal(AgentExecutionStatus.Completed,
            agents.GetExecution("codex", "relay", result.DispatchRequestId)!.Status);
    }

    [Fact]
    public async Task Cancelled_dispatch_keeps_a_sqlite_reservation_for_outcome_reconciliation()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.Normal, null);
        var gateway = new FakeGateway(AgentDispatchStatus.Accepted, request =>
        {
            Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(new AgentEvent(
                "codex", "relay", request.DispatchRequestId, 1, AgentEventType.TaskStarted,
                DateTimeOffset.UtcNow, TodoId: request.TodoId, DispatchRequestId: request.DispatchRequestId)));
            throw new OperationCanceledException("cancelled");
        });
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DispatchAsync(
            todo, "codex", "project-1", confirmed: true, cancellationToken: cancellation.Token));

        // Cancellation only cancels the local wait. The Relay may already have
        // accepted the stable request, so the reservation stays recoverable.
        Assert.Equal(TodoStatus.Active, todos.Get(todo.Id)!.Status);
        Assert.Equal(AgentExecutionStatus.Active,
            agents.GetExecution("codex", "relay", gateway.LastRequest!.DispatchRequestId)!.Status);
    }

    [Fact]
    public async Task Relay_io_failure_after_remote_start_preserves_the_authoritative_active_state()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.Normal, null);
        var gateway = new FakeGateway(AgentDispatchStatus.Accepted, request =>
        {
            Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(new AgentEvent(
                "codex", "relay", request.DispatchRequestId, 1, AgentEventType.TaskStarted,
                DateTimeOffset.UtcNow, TodoId: request.TodoId, DispatchRequestId: request.DispatchRequestId)));
            throw new IOException("connection closed after enqueue");
        });
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System);

        var result = await service.DispatchAsync(todo, "codex", "project-1", confirmed: true);

        Assert.Equal(AgentDispatchStatus.Offline, result.Status);
        Assert.Equal(TodoStatus.Active, todos.Get(todo.Id)!.Status);
        Assert.Equal(AgentExecutionStatus.Active,
            agents.GetExecution("codex", "relay", result.DispatchRequestId)!.Status);
    }

    [Fact]
    public async Task Offline_ack_after_enqueue_keeps_the_reservation_for_reconciliation()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var todo = new TodoApplicationService(todos, TimeProvider.System).Create("Ship", null, TodoPriority.Normal, null);
        var gateway = new FakeGateway(AgentDispatchStatus.Offline, connectedOverride: true);
        var service = new AgentDispatchService(todos, agents, gateway, TimeProvider.System);

        var result = await service.DispatchAsync(todo, "codex", "project-1", confirmed: true);

        Assert.Equal(AgentDispatchStatus.Offline, result.Status);
        Assert.Equal("dispatch_outcome_unknown", result.SafeError);
        Assert.Equal(TodoStatus.Active, todos.Get(todo.Id)!.Status);
        Assert.Equal(AgentExecutionStatus.Dispatching,
            agents.GetExecution("codex", "relay", result.DispatchRequestId)!.Status);
    }

    [Fact]
    public async Task Explicit_relay_rejection_uses_projector_and_refreshes_todo_ui()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var todoService = new TodoApplicationService(todos, TimeProvider.System);
        var todo = todoService.Create("Ship", null, TodoPriority.Normal, null);
        var projector = new AgentEventProjector(agents);
        var list = new TodoListViewModel(todoService, TimeProvider.System, projector: projector);
        list.Refresh();
        var service = new AgentDispatchService(
            todos, agents, new FakeGateway(AgentDispatchStatus.Failed), TimeProvider.System, projector: projector);

        var result = await service.DispatchAsync(todo, "codex", "project-1", confirmed: true);

        Assert.Equal(AgentDispatchStatus.Failed, result.Status);
        Assert.Equal(TodoStatus.Planned, todos.Get(todo.Id)!.Status);
        Assert.Equal(AgentExecutionStatus.Failed,
            agents.GetExecution("codex", "relay", result.DispatchRequestId)!.Status);
        Assert.Equal(AgentExecutionStatus.Failed, projector.Get("codex/relay/" + result.DispatchRequestId)!.Status);
        Assert.Single(list.VisibleItems);
        Assert.Equal(TodoStatus.Planned, list.VisibleItems[0].Status);
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

    private sealed class FakeGateway(
        AgentDispatchStatus status,
        Func<AgentDispatchRequest, Task>? onDispatch = null,
        bool? connectedOverride = null) : IAgentGateway
    {
        public AgentDispatchRequest? LastRequest { get; private set; }
        public bool IsConnected => connectedOverride ?? status != AgentDispatchStatus.Offline;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(IsConnected, "1", null, 0));
        public async Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (onDispatch is not null)
            {
                await onDispatch(request);
            }

            return new AgentDispatchResult(status, request.DispatchRequestId);
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
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => Array.Empty<AgentExecution>();
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => Array.Empty<AgentArchiveBatch>();
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }

    public void Dispose()
    {
        // Each test owns a unique database. Clear only this connection pool so
        // parallel App tests cannot invalidate another test's live SQLite work.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        SqliteConnection.ClearPool(connection);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _databasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
