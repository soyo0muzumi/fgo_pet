using System.IO;
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

    [Fact]
    public void Confirming_the_same_card_twice_returns_the_same_todo_without_a_second_write()
    {
        var repository = new FakeTodoRepository();
        var viewModel = CreateViewModel(repository, new TodoProposal("Review", "Keep the note"));

        var first = viewModel.Confirm();
        var second = viewModel.Confirm();

        Assert.Same(first, second);
        Assert.Equal(first.Id, viewModel.CreatedTodoId);
        Assert.True(viewModel.IsAdded);
        Assert.Equal(1, repository.SaveCount);
        Assert.Single(repository.Items);
    }

    [Fact]
    public void Removing_a_card_does_not_create_a_todo()
    {
        var repository = new FakeTodoRepository();
        var viewModel = CreateViewModel(repository, new TodoProposal("Ignore me"));

        viewModel.Remove();

        Assert.True(viewModel.IsRemoved);
        Assert.False(viewModel.IsAdded);
        Assert.Empty(repository.Items);
        Assert.Throws<InvalidOperationException>(() => viewModel.Confirm());
    }

    [Fact]
    public void A_failed_write_keeps_the_draft_and_can_be_retried()
    {
        var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
        var viewModel = CreateViewModel(repository, new TodoProposal("Retry me", "Original note"));

        Assert.Throws<IOException>(() => viewModel.Confirm());

        Assert.False(viewModel.IsAdded);
        Assert.Null(viewModel.CreatedTodoId);
        Assert.Equal("Retry me", viewModel.Title);
        Assert.Equal("Original note", viewModel.Description);
        Assert.Contains("添加失败", viewModel.ErrorText);

        repository.SaveFailure = null;
        var todo = viewModel.Confirm();

        Assert.True(viewModel.IsAdded);
        Assert.Equal(todo.Id, viewModel.CreatedTodoId);
        Assert.Empty(viewModel.ErrorText);
        Assert.Single(repository.Items);
        Assert.Equal(2, repository.SaveCount);
    }

    private static TodoProposalViewModel CreateViewModel(FakeTodoRepository repository, TodoProposal proposal) =>
        new(proposal, new TodoProposalService(new TodoApplicationService(repository, TimeProvider.System)));

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public Exception? SaveFailure { get; set; }
        public int SaveCount { get; private set; }
        public void Save(TodoItem todo)
        {
            SaveCount++;
            if (SaveFailure is not null) throw SaveFailure;
            Items.RemoveAll(item => item.Id == todo.Id);
            Items.Add(todo);
        }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Items.ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Array.Empty<TodoItem>();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }
}
