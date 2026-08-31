using FgoPet.App.Services;
using FgoPet.App.ViewModels;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.ViewModels;

public sealed class AgentDispatchDialogViewModelTests
{
    [Fact]
    public async Task Load_uses_authoritative_online_enabled_sources_and_opaque_targets()
    {
        var now = DateTimeOffset.UtcNow;
        var todo = new TodoItem("todo-1", "Ship", "Description", TodoPriority.Normal, null, now, now);
        var administration = new FakeAdministration(new AgentRelaySnapshot(
            AgentRelayConnectionState.Connected,
            RelayOnline: true,
            AppOnline: true,
            AdapterOnline: true,
            now,
            [],
            [
                new AgentApprovedSource("codex", "instance-offline", "Offline", "1", true, ["hidden"], false),
                new AgentApprovedSource("codex", "instance-disabled", "Disabled", "1", false, ["hidden"], true),
                new AgentApprovedSource("codex", "instance-live", "Live", "1", true, ["opaque-project-id", "another-id"], true),
            ]));
        var gateway = new FakeGateway();
        var service = CreateDispatchService(todo, administration, gateway);
        using var viewModel = new AgentDispatchDialogViewModel(todo, administration, service);

        await viewModel.LoadAsync();

        var source = Assert.Single(viewModel.Sources);
        Assert.Equal("instance-live", source.SourceInstanceId);
        Assert.Equal(["opaque-project-id", "another-id"], viewModel.Targets);
        Assert.Equal("opaque-project-id", viewModel.SelectedTarget);
        Assert.True(viewModel.CanConfirm);
        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task Confirm_sends_only_after_explicit_command_and_includes_selected_instance()
    {
        var now = DateTimeOffset.UtcNow;
        var todo = new TodoItem("todo-2", "Ship", null, TodoPriority.High, null, now, now);
        var source = new AgentApprovedSource("codex", "instance-live", "Live", "1", true, ["opaque-project-id", "another-id"], true);
        var administration = new FakeAdministration(new AgentRelaySnapshot(
            AgentRelayConnectionState.Connected, true, true, true, now, [], [source]));
        var gateway = new FakeGateway();
        var service = CreateDispatchService(todo, administration, gateway);
        using var viewModel = new AgentDispatchDialogViewModel(todo, administration, service);

        await viewModel.LoadAsync();
        Assert.Null(gateway.LastRequest);

        viewModel.SelectedTarget = "another-id";
        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(gateway.LastRequest);
        Assert.Equal("codex", gateway.LastRequest!.SourceType);
        Assert.Equal("instance-live", gateway.LastRequest.SourceInstanceId);
        Assert.Equal("another-id", gateway.LastRequest.TargetId);
        Assert.Equal(AgentDispatchStatus.Accepted, viewModel.LastResult?.Status);
    }

    private static AgentDispatchService CreateDispatchService(
        TodoItem todo,
        IAgentRelayAdministration administration,
        FakeGateway gateway) => new(
        new FakeTodoRepository(todo),
        new FakeAgentRepository(),
        gateway,
        TimeProvider.System,
        administration);

    private sealed class FakeAdministration(AgentRelaySnapshot snapshot) : IAgentRelayAdministration
    {
        public Task<AgentRelaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<AgentRelaySnapshot> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdatePermissionsAsync(string sourceType, string sourceInstanceId, IReadOnlyList<string> targetIds, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RevokeSourceAsync(string sourceType, string sourceInstanceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeGateway : IAgentGateway
    {
        public AgentDispatchRequest? LastRequest { get; private set; }
        public bool IsConnected => true;
        public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AgentGatewayStatus(true, "1", null, 0));
        public Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentDispatchResult(AgentDispatchStatus.Accepted, request.DispatchRequestId, TaskId: request.DispatchRequestId, SourceInstance: request.SourceInstanceId));
        }
        public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new AgentOpenTaskResult(AgentOpenTaskStatus.Unsupported));
        public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(IReadOnlyList<AgentExecution> knownExecutions, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class FakeTodoRepository(TodoItem todo) : ITodoRepository
    {
        private TodoItem _todo = todo;
        public void Save(TodoItem value) => _todo = value;
        public TodoItem? Get(string id) => _todo.Id == id ? _todo : null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => status is null || _todo.Status == status ? [_todo] : [];
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => [];
        public void Delete(string id) { }
        public void ClearAgentTodoData() { }
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public void SaveExecution(AgentExecution execution) { }
        public AgentExecution? GetExecution(string id) => null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => [];
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => AgentEventApplyResult.Applied;
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => [];
    }
}
