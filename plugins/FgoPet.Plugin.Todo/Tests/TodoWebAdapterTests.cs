using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Plugin.Todo.Tests;

public sealed class TodoWebAdapterTests
{
    // Peek keeps items created on the local "today", so the fixture clock must agree with `today`
    // instead of following the real system date (which silently broke the test on 2026-09-29).
    private static readonly TimeProvider FixtureClock =
        new FixedTimeProvider(new DateTimeOffset(2026, 9, 28, 2, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Peek_uses_local_due_dates_and_current_todo_status_without_persisting_a_view_state()
    {
        var service = new TodoApplicationService(new Repository(), FixtureClock);
        using var feed = new TodoChangeFeed(service);
        var adapter = new TodoWebAdapter(service, feed, FixtureClock);
        var today = new DateOnly(2026, 9, 28);
        var dueToday = service.Create("today", null, TodoPriority.Normal, new DateTimeOffset(2026, 9, 28, 0, 30, 0, TimeSpan.FromHours(9)));
        service.Create("later", null, TodoPriority.Normal, new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.FromHours(9)));
        var inbox = service.Create("inbox", null, TodoPriority.Normal, null);

        var snapshot = adapter.GetPeekSnapshot(today, TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(8), "fixture", "fixture"));

        Assert.Equal(3, snapshot.Revision);
        Assert.Equal(2, snapshot.Tasks.Count);
        Assert.Contains(snapshot.Tasks, item => item.Id == dueToday.Id);
        Assert.Contains(snapshot.Tasks, item => item.Id == inbox.Id);
    }

    [Fact]
    public void Quick_add_writes_directly_while_a_model_proposal_waits_for_confirmation()
    {
        var service = new TodoApplicationService(new Repository(), FixtureClock);
        using var feed = new TodoChangeFeed(service);
        var adapter = new TodoWebAdapter(service, feed);
        var proposals = new TodoProposalService(service);

        var direct = adapter.QuickAdd("typed by user");
        var draft = Assert.Single(proposals.Parse("""{"todos":[{"title":"model suggestion"}]}"""));
        Assert.Equal(1, feed.Revision);
        Assert.Null(service.ListActive().SingleOrDefault(item => item.Title == "model suggestion"));
        proposals.Confirm(draft);

        Assert.Equal("typed by user", direct.Title);
        Assert.Equal(2, feed.Revision);
        Assert.Equal(2, service.ListActive().Count);
    }

    [Fact]
    public void Completed_today_counts_the_users_local_day_across_the_utc_boundary()
    {
        var repository = new Repository();
        var completed = new DateTimeOffset(2026, 9, 27, 17, 30, 0, TimeSpan.Zero);
        repository.Save(new TodoItem("local-today", "done", null, TodoPriority.Normal, null,
            completed.AddHours(-1), completed, TodoStatus.Completed, completed));
        var tomorrow = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
        repository.Save(new TodoItem("local-tomorrow", "later", null, TodoPriority.Normal, null,
            tomorrow.AddHours(-1), tomorrow, TodoStatus.Completed, tomorrow));
        var service = new TodoApplicationService(repository, TimeProvider.System);
        using var feed = new TodoChangeFeed(service);

        var snapshot = new TodoWebAdapter(service, feed).GetPeekSnapshot(new DateOnly(2026, 9, 28),
            TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(8), "fixture", "fixture"));

        Assert.Equal(1, snapshot.CompletedToday);
    }

    [Fact]
    public void Workspace_reads_authoritative_lists_and_rejects_stale_edit_and_delete_tokens()
    {
        var service = new TodoApplicationService(new Repository(), FixtureClock);
        using var feed = new TodoChangeFeed(service);
        var adapter = new TodoWebAdapter(service, feed);
        var created = adapter.CreateTask("original", "note");
        var snapshot = adapter.QueryTasks();
        Assert.Equal(1, snapshot.Revision);
        Assert.Equal(created.Id, Assert.Single(snapshot.Active).Id);
        Assert.Empty(snapshot.Completed);
        Assert.Equal(created.Etag, adapter.GetTask(created.Id)!.Etag);

        var edited = adapter.UpdateTask(created.Id, created.Etag, "edited", "kept");
        Assert.Equal("edited", service.Get(created.Id)!.Title);
        Assert.NotEqual(created.Etag, edited.Etag);
        Assert.Throws<InvalidOperationException>(() => adapter.UpdateTask(created.Id, created.Etag,
            "stale overwrite", null));
        Assert.Throws<InvalidOperationException>(() => adapter.DeleteTask(created.Id, created.Etag));
        Assert.Throws<InvalidOperationException>(() => adapter.SetCompletion(created.Id, true,
            etag: created.Etag));
        Assert.Equal("edited", service.Get(created.Id)!.Title);

        adapter.DeleteTask(created.Id, edited.Etag);
        Assert.Null(service.Get(created.Id));
        Assert.Equal(3, feed.Revision);
    }

    [Fact]
    public void Completion_routes_through_the_application_service_and_preserves_step_confirmation()
    {
        var service = new TodoApplicationService(new Repository(), FixtureClock);
        using var feed = new TodoChangeFeed(service);
        var adapter = new TodoWebAdapter(service, feed);
        var item = service.Create("task", null, TodoPriority.Normal, null, ["incomplete"]);

        Assert.Throws<InvalidOperationException>(() => adapter.SetCompletion(item.Id, true));
        Assert.Equal(1, feed.Revision);
        var completed = adapter.SetCompletion(item.Id, true, confirmIncompleteSteps: true);
        Assert.Equal(TodoStatus.Completed, completed.Status);
        Assert.Equal(TodoStatus.Planned, adapter.SetCompletion(item.Id, false).Status);
        Assert.Equal(3, feed.Revision);
    }

    [Fact]
    public void Workspace_views_are_derived_from_existing_status_and_local_due_day_across_dst()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var today = new DateOnly(2026, 3, 8);
        var now = new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero);
        TodoTaskDetail Task(string id, TodoStatus status, DateTimeOffset? due) =>
            new(id, id, null, TodoPriority.Normal, status, due, now, now, null, [], id, status == TodoStatus.Active);
        var active = Task("active", TodoStatus.Active, null);
        var inbox = Task("inbox", TodoStatus.Planned, null);
        var dueToday = Task("local-today", TodoStatus.Planned,
            new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero));
        var tomorrow = Task("local-tomorrow", TodoStatus.Planned,
            new DateTimeOffset(2026, 3, 9, 7, 30, 0, TimeSpan.Zero));

        var projection = TodoWorkspaceProjection.Project([active, inbox, dueToday, tomorrow], today, zone);

        Assert.Equal(["active", "local-today"], projection.Today.Select(item => item.Id));
        Assert.Equal(["inbox"], projection.Inbox.Select(item => item.Id));
        Assert.Equal(["local-tomorrow"], projection.Upcoming.Select(item => item.Id));
        Assert.Equal(4, projection.All.Count);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class Repository : ITodoRepository
    {
        private readonly List<TodoItem> _items = [];
        public void Save(TodoItem todo) { _items.RemoveAll(item => item.Id == todo.Id); _items.Add(todo); }
        public TodoItem? Get(string id) => _items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) =>
            _items.Where(item => status is null || item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => [];
        public void Delete(string id) => _items.RemoveAll(item => item.Id == id);
    }
}
