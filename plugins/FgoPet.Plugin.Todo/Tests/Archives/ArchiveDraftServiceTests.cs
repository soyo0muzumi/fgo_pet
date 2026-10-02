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
        var unrelated = new TodoItem("unrelated", "Keep", null, TodoPriority.Normal, null, Now(), Now());
        todos.Save(unrelated);
        var archives = new FakeArchiveRepository(todos);
        var service = new ArchiveDraftService(todos, archives, TimeProvider.System);

        var draft = service.CreateDraft("codex", new[] { completed }, "Delivered the bridge");
        Assert.Equal(1, draft.CoveredTodoCount);
        Assert.Contains("Finished", draft.ModelInput, StringComparison.Ordinal);
        Assert.DoesNotContain("todo-1", draft.ModelInput, StringComparison.Ordinal);

        Assert.Equal("工作归档", draft.Title);
        Assert.Empty(archives.Saved);
        Assert.Equal(2, todos.Items.Count);
        IArchiveDraftConfirmation port = service;
        port.Confirm(draft with { Title = "Edited archive", Summary = "Edited summary" });

        var saved = Assert.Single(archives.Saved);
        Assert.Equal(draft.ArchiveId, saved.ArchiveId);
        Assert.Equal("Edited archive", saved.Title);
        Assert.Equal("Edited summary", saved.Summary);
        Assert.Equal<string>(["todo-1"], saved.CoveredTodoKeys);
        Assert.Equal(DateOnly.FromDateTime(completed.CompletedAt!.Value.LocalDateTime.Date), saved.StartedOn);
        Assert.Equal("unrelated", Assert.Single(todos.Items).Id);
    }

    [Fact]
    public void Draft_rejects_unfinished_todos()
    {
        var todo = new TodoItem("todo-1", "Running", null, TodoPriority.Normal, null, Now(), Now());
        var service = new ArchiveDraftService(new FakeTodoRepository(), new FakeArchiveRepository(), TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() => service.CreateDraft("codex", new[] { todo }, "No"));
    }

    [Fact]
    public void Archive_contract_preserves_existing_record_copy_semantics_and_provenance()
    {
        var original = new ArchiveDraft("archive-fixture", "fixture", ["todo-a", "todo-b"],
            new(2026, 1, 3), "Original", new(2026, 1, 1), new(2026, 1, 2), "Summary",
            ["Outcome"], "Synthetic model input");
        var edited = original with { Title = "Edited", Summary = "Edited summary" };
        Assert.Equal(2, edited.CoveredTodoCount);
        Assert.Equal(original.ArchiveId, edited.ArchiveId);
        Assert.Equal(original.SourceType, edited.SourceType);
        Assert.Same(original.CoveredTodoKeys, edited.CoveredTodoKeys);
        Assert.Same(original.Outcomes, edited.Outcomes);
        Assert.Equal(original.ModelInput, edited.ModelInput);
        Assert.Equal(original.ArchiveDate, edited.ArchiveDate);
        Assert.Equal(original.StartedOn, edited.StartedOn);
        Assert.Equal(original.CompletedOn, edited.CompletedOn);
        Assert.Equal("Original", original.Title);
        Assert.Equal("Summary", original.Summary);
    }

    [Fact]
    public void Long_archive_confirmation_replaces_only_the_selected_work_summaries()
    {
        var store = new MemoryLongArchiveSummaryStore();
        var service = new LongArchiveService(store, TimeProvider.System);
        var first = new WorkArchive("archive-1", new[] { "todo-1" }, new[] { "codex" }, DateOnly.FromDateTime(DateTime.Today), "First", DateTimeOffset.UtcNow);
        var second = new WorkArchive("archive-2", new[] { "todo-2" }, new[] { "codex" }, DateOnly.FromDateTime(DateTime.Today), "Second", DateTimeOffset.UtcNow);
        var draft = service.CreateDraft(new[] { first, second }, "Bridge history");

        service.Confirm(draft);

        var saved = Assert.Single(store.Items);
        Assert.Equal("Bridge history", saved.Title);
        Assert.Equal(new[] { "archive-1", "archive-2" }, saved.CoveredArchiveIds);
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
