using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.App.ViewModels;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;
using Xunit;

namespace FgoPet.App.Tests.ViewModels;

public sealed class TodoListViewModelTests
{
    [Fact]
    public void Todo_list_keeps_overflow_rows_for_the_existing_scroll_container()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        for (var index = 0; index < 10; index++)
        {
            service.Create($"Todo {index}", null, TodoPriority.Normal, null);
        }

        var viewModel = new TodoListViewModel(service, TimeProvider.System);
        viewModel.Refresh();

        Assert.Equal(10, viewModel.VisibleItems.Count);
        Assert.True(viewModel.HasOverflow);
        Assert.Equal(TodoListTab.Todo, viewModel.SelectedTab);
    }

    [Fact]
    public void History_and_today_filter_are_separate_from_the_two_content_tabs()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var now = DateTimeOffset.Now;
        repository.Items.Add(new TodoItem("today", "Today", null, TodoPriority.Normal, null, now, now, TodoStatus.Completed, now));
        repository.Items.Add(new TodoItem("yesterday", "Yesterday", null, TodoPriority.Normal, null, now.AddDays(-1), now.AddDays(-1), TodoStatus.Completed, now.AddDays(-1)));
        var viewModel = new TodoListViewModel(service, TimeProvider.System);

        viewModel.SelectTab(TodoListTab.History);
        viewModel.Refresh();
        Assert.Equal(TodoListTab.History, viewModel.SelectedTab);
        Assert.Equal(2, viewModel.VisibleItems.Count);

        viewModel.OnlyToday = true;
        viewModel.Refresh();
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal("Today", viewModel.VisibleItems[0].Title);
    }

    [Fact]
    public void Agent_completion_refreshes_an_open_todo_projection()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var todo = service.Create("Ship", null, TodoPriority.Normal, null);
        var agents = new FakeAgentRepository(repository);
        agents.SaveExecution(new AgentExecution(
            "execution-1", todo.Id, "codex", "source-1", "task-1", "dispatch-1", DateTimeOffset.UtcNow));
        var projector = new AgentEventProjector(agents);
        var viewModel = new TodoListViewModel(service, TimeProvider.System, projector: projector);
        viewModel.Refresh();

        projector.Apply(new AgentEvent(
            "codex", "source-1", "task-1", 1, AgentEventType.TaskStarted,
            DateTimeOffset.UtcNow, TodoId: todo.Id));
        Assert.Equal(TodoStatus.Active, repository.Get(todo.Id)!.Status);
        Assert.Single(viewModel.VisibleItems);

        projector.Apply(new AgentEvent(
            "codex", "source-1", "task-1", 2, AgentEventType.TaskCompleted,
            DateTimeOffset.UtcNow.AddMinutes(1), TodoId: todo.Id));

        Assert.Empty(viewModel.VisibleItems);
        viewModel.SelectTab(TodoListTab.History);
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(TodoStatus.Completed, viewModel.VisibleItems[0].Status);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public void Save(TodoItem todo)
        {
            Items.RemoveAll(item => item.Id == todo.Id);
            Items.Add(todo);
        }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => status is null ? Items.ToArray() : Items.Where(item => item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Items.Where(item => item.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date).ToArray();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }

    private sealed class FakeAgentRepository(FakeTodoRepository todos) : IAgentRepository
    {
        private AgentExecution? _execution;

        public void SaveExecution(AgentExecution execution) => _execution = execution;
        public AgentExecution? GetExecution(string id) => _execution?.Id == id ? _execution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => _execution;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => _execution is { IsNonTerminal: true }
            ? new[] { _execution }
            : Array.Empty<AgentExecution>();

        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent)
        {
            if (_execution is null)
            {
                throw new KeyNotFoundException();
            }

            _execution = agentEvent.EventType switch
            {
                AgentEventType.TaskStarted => _execution.MarkStarted(agentEvent.OccurredAt),
                AgentEventType.TaskCompleted => _execution.MarkCompleted(agentEvent.OccurredAt),
                _ => _execution,
            };
            var todo = todos.Get(_execution.TodoId);
            if (todo is not null)
            {
                todos.Save(agentEvent.EventType == AgentEventType.TaskCompleted
                    ? todo.Complete(agentEvent.OccurredAt)
                    : todo.Status == TodoStatus.Planned ? todo.Activate(agentEvent.OccurredAt) : todo);
            }

            return AgentEventApplyResult.Applied;
        }

        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => Array.Empty<PersistedAgentConnection>();
    }
}
