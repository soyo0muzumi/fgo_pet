using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Plugin.Todo.Tests;

public sealed class TodoChangeFeedTests
{
    [Fact]
    public void Existing_service_writes_and_confirmed_proposals_share_one_monotonic_feed()
    {
        var service = new TodoApplicationService(new Repository(), TimeProvider.System);
        using var feed = new TodoChangeFeed(service);
        var changes = new List<TodoChange>();
        using var subscription = feed.Subscribe(changes.Add);
        var proposalService = new TodoProposalService(service);

        var direct = service.Create("direct", null, TodoPriority.Normal, null);
        service.Update(direct.Id, "edited", null);
        service.Complete(direct.Id);
        service.Reopen(direct.Id);
        service.Delete(direct.Id);
        var proposal = Assert.Single(proposalService.Parse("""{"todos":[{"title":"confirmed"}]}"""));
        Assert.Equal(5, feed.Revision);
        proposalService.Confirm(proposal);

        Assert.Equal(6, feed.Revision);
        Assert.Equal(Enumerable.Range(1, 6).Select(value => (long)value), changes.Select(change => change.Revision));
        Assert.All(changes, change => Assert.Null(change.AffectedIds));
    }

    [Fact]
    public void Subscribe_before_snapshot_retries_when_a_write_races_the_read()
    {
        var service = new TodoApplicationService(new Repository(), TimeProvider.System);
        using var feed = new TodoChangeFeed(service);
        var observed = new List<TodoChange>();
        using var subscription = feed.Subscribe(observed.Add);
        var reads = 0;

        var snapshot = feed.ReadStableSnapshot(() =>
        {
            if (++reads == 1) service.Create("racing write", null, TodoPriority.Normal, null);
            return service.ListActive();
        });

        Assert.Equal(2, reads);
        Assert.Equal(1, snapshot.Revision);
        Assert.Equal("racing write", Assert.Single(snapshot.Value).Title);
        Assert.Single(observed);
    }

    [Fact]
    public void Disposing_a_surface_subscription_and_feed_stops_late_notifications()
    {
        var service = new TodoApplicationService(new Repository(), TimeProvider.System);
        using var feed = new TodoChangeFeed(service);
        var notifications = 0;
        var subscription = feed.Subscribe(_ => notifications++);
        service.Create("one", null, TodoPriority.Normal, null);
        subscription.Dispose();
        service.Create("two", null, TodoPriority.Normal, null);
        feed.Dispose();
        service.Create("three", null, TodoPriority.Normal, null);

        Assert.Equal(1, notifications);
        Assert.Equal(2, feed.Revision);
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
