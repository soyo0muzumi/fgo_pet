using FgoPet.App.Services;
using FgoPet.App.Dialogue;
using FgoPet.App.ViewModels;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.ViewModels;

public sealed class TodoProposalViewModelTests
{
    [Fact]
    public void Confirming_a_proposal_is_the_only_operation_that_writes_a_todo()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System));
        var proposal = Assert.Single(service.Parse("""{"title":"Review","priority":"normal"}"""));
        var viewModel = new TodoProposalViewModel(proposal, service);

        Assert.Empty(repository.Items);
        var todo = viewModel.Confirm();

        Assert.Equal("Review", todo.Title);
        Assert.Single(repository.Items);
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public void Save(TodoItem todo) { Items.RemoveAll(item => item.Id == todo.Id); Items.Add(todo); }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Items.ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }
}
