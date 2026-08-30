using FgoPet.App.Archives;
using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.App.Tests.Archives;

public sealed class ArchiveDraftServiceTests
{
    [Fact]
    public void Draft_reads_only_completed_covered_todos_and_confirmation_cleans_them()
    {
        var todos = new FakeTodoRepository();
        var completed = new TodoItem("todo-1", "Finished", null, TodoPriority.Normal, null, Now(), Now(), TodoStatus.Completed, Now());
        todos.Save(completed);
        var archives = new FakeArchiveRepository(todos);
        var service = new ArchiveDraftService(todos, archives, TimeProvider.System);

        var draft = service.CreateDraft("codex", new[] { completed }, "Delivered the bridge");
        Assert.Equal(1, draft.CoveredTodoCount);
        Assert.Contains("Finished", draft.ModelInput, StringComparison.Ordinal);
        Assert.DoesNotContain("todo-1", draft.ModelInput, StringComparison.Ordinal);

        service.Confirm(draft);

        Assert.Single(archives.Saved);
        Assert.Empty(todos.Items);
    }

    [Fact]
    public void Draft_rejects_unfinished_todos()
    {
        var todo = new TodoItem("todo-1", "Running", null, TodoPriority.Normal, null, Now(), Now());
        var service = new ArchiveDraftService(new FakeTodoRepository(), new FakeArchiveRepository(), TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => service.CreateDraft("codex", new[] { todo }, "No"));
    }

    private static DateTimeOffset Now() => DateTimeOffset.UtcNow;

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public void Save(TodoItem todo) { Items.RemoveAll(item => item.Id == todo.Id); Items.Add(todo); }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => status is null ? Items.ToArray() : Items.Where(item => item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Items.Where(item => item.Status == TodoStatus.Completed).ToArray();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }

    private sealed class FakeArchiveRepository : IWorkArchiveRepository
    {
        private readonly FakeTodoRepository? _todos;
        public List<WorkArchive> Saved { get; } = new();
        public FakeArchiveRepository(FakeTodoRepository? todos = null) => _todos = todos;
        public void Confirm(WorkArchive archive)
        {
            Saved.RemoveAll(item => item.ArchiveId == archive.ArchiveId);
            Saved.Add(archive);
            foreach (var todoKey in archive.CoveredTodoKeys) _todos?.Delete(todoKey);
        }
        public WorkArchive? Get(string archiveId) => Saved.SingleOrDefault(item => item.ArchiveId == archiveId);
        public IReadOnlyList<WorkArchive> List() => Saved.ToArray();
        public IReadOnlyList<string> LoadCoveredTodoKeys(string archiveId) => Get(archiveId)?.CoveredTodoKeys ?? Array.Empty<string>();
    }
}
