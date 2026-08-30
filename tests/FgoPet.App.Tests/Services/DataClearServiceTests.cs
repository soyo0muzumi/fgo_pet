using FgoPet.App.Services;
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
}
