using FgoPet.App.Services;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class TodoApplicationServiceTests
{
    [Fact]
    public void Creating_a_todo_persists_it_without_selecting_or_dispatching_an_agent()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);

        var todo = service.Create("Write tests", "Keep the change small.", TodoPriority.High, null);

        Assert.Equal(TodoStatus.Planned, todo.Status);
        Assert.True(todo.CanDispatch);
        Assert.Same(todo, repository.Saved);
    }

    [Fact]
    public void Deleting_an_active_todo_is_rejected_by_the_application_service()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var todo = service.Create("Running", null, TodoPriority.Normal, null).Activate(DateTimeOffset.UtcNow);
        repository.Saved = todo;

        Assert.Throws<InvalidOperationException>(() => service.Delete(todo.Id));
        Assert.Null(repository.DeletedId);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public TodoItem? Saved { get; set; }
        public string? DeletedId { get; private set; }
        public void Save(TodoItem todo) => Saved = todo;
        public TodoItem? Get(string id) => Saved?.Id == id ? Saved : null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Saved is null || status is not null && Saved.Status != status ? Array.Empty<TodoItem>() : new[] { Saved };
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Saved?.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date ? new[] { Saved } : Array.Empty<TodoItem>();
        public void Delete(string id) { DeletedId = id; Saved = null; }
        public void ClearAgentTodoData() => Saved = null;
    }
}
