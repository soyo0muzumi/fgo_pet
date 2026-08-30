using FgoPet.App.Services;
using FgoPet.App.ViewModels;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.ViewModels;

public sealed class TodoListViewModelTests
{
    [Fact]
    public void Todo_list_shows_at_most_eight_timeline_rows_and_reports_overflow()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        for (var index = 0; index < 10; index++)
        {
            service.Create($"Todo {index}", null, TodoPriority.Normal, null);
        }

        var viewModel = new TodoListViewModel(service, TimeProvider.System);
        viewModel.Refresh();

        Assert.Equal(8, viewModel.VisibleItems.Count);
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
}
