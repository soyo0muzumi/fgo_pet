using FgoPet.App.Services;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Services;

public sealed class TodoApplicationServiceTests
{
    [Fact]
    public void Repeated_confirmation_of_one_proposal_creates_only_one_todo()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var proposal = new FgoPet.App.ViewModels.TodoProposalViewModel(
            new FgoPet.App.Dialogue.TodoProposal("Learn Transformer", "1. Attention\n2. Architecture", TodoPriority.Normal, null),
            new FgoPet.App.Dialogue.TodoProposalService(service));
        var first = proposal.Confirm();
        var second = proposal.Confirm();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public void Fresh_settings_default_to_light()
    {
        Assert.Equal(FgoPet.Core.Settings.AppTheme.FgoLight, FgoPet.Core.Settings.AppSettings.Defaults.Theme);
    }
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

    [Fact]
    public void Editing_preserves_existing_priority_due_date_and_identity()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var due = DateTimeOffset.UtcNow.AddDays(2);
        var original = service.Create("Before", "Steps", TodoPriority.High, due);
        var edited = service.Update(original.Id, "After", "1. Learn\n2. Explain");
        Assert.Equal(original.Id, edited.Id);
        Assert.Equal(original.Priority, edited.Priority);
        Assert.Equal(due, edited.DueAt);
        Assert.Equal(original.CreatedAt, edited.CreatedAt);
    }

    [Fact]
    public void Manual_completion_can_be_undone_but_active_execution_cannot_be_completed()
    {
        var repository = new FakeTodoRepository();
        var service = new TodoApplicationService(repository, TimeProvider.System);
        var todo = service.Create("Learn", null, TodoPriority.Normal, null);
        var completed = service.Complete(todo.Id);
        Assert.Equal(TodoStatus.Completed, repository.Saved!.Status);
        service.UndoCompletion(completed);
        Assert.Equal(TodoStatus.Planned, repository.Saved!.Status);
        repository.Saved = repository.Saved.Activate(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => service.Complete(todo.Id));
        Assert.Throws<InvalidOperationException>(() => service.Update(todo.Id, "Changed", null));
        Assert.Equal(TodoStatus.Active, repository.Saved.Status);
    }
    private sealed class FakeTodoRepository : ITodoRepository
    {
        public int SaveCount { get; private set; }
        public TodoItem? Saved { get; set; }
        public string? DeletedId { get; private set; }
        public void Save(TodoItem todo) { Saved = todo; SaveCount++; }
        public TodoItem? Get(string id) => Saved?.Id == id ? Saved : null;
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Saved is null || status is not null && Saved.Status != status ? Array.Empty<TodoItem>() : new[] { Saved };
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Saved?.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date ? new[] { Saved } : Array.Empty<TodoItem>();
        public void Delete(string id) { DeletedId = id; Saved = null; }
        public void ClearAgentTodoData() => Saved = null;
    }
}
