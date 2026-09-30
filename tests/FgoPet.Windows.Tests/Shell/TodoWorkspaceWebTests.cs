using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Services;
using FgoPet.App.Windowing;
using FgoPet.Core.Todo;
using FgoPet.Plugin.Todo.Desktop;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class TodoWorkspaceWebTests
{
    [Fact]
    public async Task Web_workspace_uses_the_existing_service_and_releases_its_subscription()
    {
        await StaRunner.RunAsync(async () =>
        {
            var service = new TodoApplicationService(new Repository(), TimeProvider.System);
            using var feed = new TodoChangeFeed(service);
            var factory = new TodoWorkspaceWebFactory(new TodoWebAdapter(service, feed), feed,
                Path.Combine(Path.GetTempPath(), "fgopet-workspace-" + Guid.NewGuid().ToString("N")));
            var surface = factory.CreateView();
            var window = new Window { Content = surface, Width = 560, Height = 640,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Show();
            await WaitUntil(() => surface.Host.State == WebSurfaceState.Ready);
            Assert.Equal(1, feed.ActiveSubscriptionCount);
            var web = Assert.Single(surface.Host.Children.OfType<WebView2>());
            await WaitUntil(async () => await Script(web, "document.querySelector('#summary').textContent") == "0 项进行中 · 0 项已完成");

            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#new-title').value='workspace task'; document.querySelector('#new-task').requestSubmit()");
            await WaitUntil(() => service.ListActive().Any(item => item.Title == "workspace task"));
            var created = Assert.Single(service.ListActive());
            await WaitUntil(async () => (await Script(web, "document.querySelector('#workspace-tasks').textContent")).Contains("workspace task", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("if (document.querySelector('.task-heading button').getAttribute('aria-expanded') === 'false') document.querySelector('.task-heading button').click()");
            Assert.True(await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-actions') !== null") == "true",
                $"Row: {await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#workspace-tasks').innerHTML")}; " +
                $"Notice: {await Script(web, "document.querySelector('#notice').textContent")}");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-actions button:nth-child(2)').click()");
            Assert.Equal("true", await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-editor') !== null"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-editor input').value='edited task'; document.querySelector('.task-editor').requestSubmit()");
            var editDeadline = DateTime.UtcNow.AddSeconds(5);
            while (service.Get(created.Id)?.Title != "edited task" && DateTime.UtcNow < editDeadline)
                await Task.Delay(25);
            Assert.True(service.Get(created.Id)?.Title == "edited task",
                $"Edit did not save. Notice: {await Script(web, "document.querySelector('#notice').textContent")}; " +
                $"Editor: {await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-editor') !== null")}; " +
                $"Actual title: {service.Get(created.Id)?.Title}; " +
                $"Page: {await Script(web, "document.querySelector('#workspace-tasks').textContent")}");
            await WaitUntil(async () => (await Script(web, "document.querySelector('#workspace-tasks').textContent")).Contains("edited task", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-actions button').click()");
            await WaitUntil(() => service.Get(created.Id)?.Status == TodoStatus.Completed);
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#undo-zone button') !== null") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#undo-zone button').click()");
            await WaitUntil(() => service.Get(created.Id)?.Status == TodoStatus.Planned);
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-actions button') !== null") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-actions button').click()");
            await WaitUntil(() => service.Get(created.Id)?.Status == TodoStatus.Completed);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#completed-tab').click()");
            await WaitUntil(async () => (await Script(web, "document.querySelector('#workspace-tasks').textContent")).Contains("edited task", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("if (document.querySelector('.task-heading button').getAttribute('aria-expanded') === 'false') document.querySelector('.task-heading button').click(); document.querySelector('.task-actions button').click()");
            await WaitUntil(() => service.Get(created.Id)?.Status == TodoStatus.Planned);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#active-tab').click()");
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-editor') !== null") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-editor input').value='first step'; document.querySelector('.step-editor').requestSubmit()");
            await WaitUntil(() => service.Get(created.Id)?.Steps.Count == 1);
            await WaitUntil(async () => (await Script(web, "document.querySelector('#workspace-tasks').textContent")).Contains("first step", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-editor input').value='second step'; document.querySelector('.step-editor').requestSubmit()");
            await WaitUntil(() => service.Get(created.Id)?.Steps.Count == 2);
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.step-list li').length") == "2");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-list li:nth-child(2) .step-actions button:nth-child(3)').click()");
            await WaitUntil(() => service.Get(created.Id)?.Steps[0].Title == "second step");
            await WaitUntil(async () => (await Script(web, "document.querySelector('.step-list li:first-child').textContent")).Contains("second step", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-list li:first-child input[type=checkbox]').click()");
            await WaitUntil(() => service.Get(created.Id)?.Steps[0].IsCompleted == true);
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-list li:first-child input[type=checkbox]').checked") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-list li:first-child .step-actions button').click()");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-list li:first-child .step-editor input').value='renamed step'; document.querySelector('.step-list li:first-child .step-editor').requestSubmit()");
            await WaitUntil(() => service.Get(created.Id)?.Steps[0].Title == "renamed step");
            await WaitUntil(async () => (await Script(web, "document.querySelector('.step-list li:first-child').textContent")).Contains("renamed step", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.step-list li:last-child .step-actions button:nth-child(2)').click(); document.querySelector('.step-list li:last-child .step-actions button:last-child').click()");
            await WaitUntil(() => service.Get(created.Id)?.Steps.Count == 1);
            surface.Navigate(new WorkspaceNavigation(WorkspaceNavigationKind.ExistingItem, created.Id));
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.is-highlighted') !== null") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-actions button:nth-child(2)').click()");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-editor input').value='my draft'; document.querySelector('.task-editor input').dispatchEvent(new Event('input', {bubbles:true}))");
            service.Update(created.Id, "external edit", null);
            await WaitUntil(async () => await Script(web, "document.querySelector('.task-editor input').value") == "my draft");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-editor').requestSubmit()");
            await WaitUntil(async () => (await Script(web, "document.querySelector('#notice').textContent")).Contains("草稿已保留", StringComparison.Ordinal));
            Assert.Equal("external edit", service.Get(created.Id)!.Title);
            Assert.Equal("my draft", await Script(web, "document.querySelector('.task-editor input').value"));

            window.Close();
            await WaitUntil(() => surface.Host.State == WebSurfaceState.Closed);
            Assert.Equal(WebSurfaceState.Closed, surface.Host.State);
            Assert.Equal(0, feed.ActiveSubscriptionCount);
        });
    }

    [Fact]
    public async Task Typed_workspace_commands_reject_stale_edits()
    {
        var service = new TodoApplicationService(new Repository(), TimeProvider.System);
        using var feed = new TodoChangeFeed(service);
        var adapter = new TodoWebAdapter(service, feed);
        var factory = new TodoWorkspaceWebFactory(adapter, feed, Path.GetTempPath());
        var item = service.Create("original", null, TodoPriority.Normal, null);
        var etag = adapter.GetTask(item.Id)!.Etag;
        service.Update(item.Id, "newer", null);
        var result = await factory.HandleCommandAsync(Message("updateTask",
            $$"""{"id":"{{item.Id}}","etag":"{{etag}}","title":"stale"}"""), default);
        Assert.False(result.Success);
        Assert.Equal("TODO_CONFLICT", result.ErrorCode);
        Assert.Equal("newer", service.Get(item.Id)!.Title);
        var current = adapter.GetTask(item.Id)!;
        var saved = await factory.HandleCommandAsync(Message("updateTask",
            $$"""{"id":"{{item.Id}}","etag":"{{current.Etag}}","title":"fresh","description":""}"""), default);
        Assert.True(saved.Success, saved.ErrorCode);
        Assert.Equal("fresh", service.Get(item.Id)!.Title);
    }

    [Fact]
    public async Task Undo_token_obeys_the_original_completion_snapshot_and_eight_second_limit()
    {
        var service = new TodoApplicationService(new Repository(), TimeProvider.System);
        using var feed = new TodoChangeFeed(service);
        var adapter = new TodoWebAdapter(service, feed);
        var clock = new ManualClock();
        var factory = new TodoWorkspaceWebFactory(adapter, feed, Path.GetTempPath(), clock: clock);
        var first = service.Create("first", null, TodoPriority.Normal, null);
        var firstEtag = adapter.GetTask(first.Id)!.Etag;
        var completion = await factory.HandleCommandAsync(Message("setCompletion",
            $$"""{"id":"{{first.Id}}","etag":"{{firstEtag}}","completed":true}"""), default);
        Assert.True(completion.Success, completion.ErrorCode);
        var token = JsonSerializer.SerializeToElement(completion.Payload).GetProperty("undoToken").GetString();
        Assert.NotNull(token);
        var undo = await factory.HandleCommandAsync(Message("undoCompletion",
            $$"""{"undoToken":"{{token}}"}"""), default);
        Assert.True(undo.Success, undo.ErrorCode);
        Assert.Equal(TodoStatus.Planned, service.Get(first.Id)!.Status);
        var replay = await factory.HandleCommandAsync(Message("undoCompletion",
            $$"""{"undoToken":"{{token}}"}"""), default);
        Assert.Equal("TODO_CONFLICT", replay.ErrorCode);

        var secondEtag = adapter.GetTask(first.Id)!.Etag;
        completion = await factory.HandleCommandAsync(Message("setCompletion",
            $$"""{"id":"{{first.Id}}","etag":"{{secondEtag}}","completed":true}"""), default);
        token = JsonSerializer.SerializeToElement(completion.Payload).GetProperty("undoToken").GetString();
        clock.Advance(TimeSpan.FromSeconds(9));
        var expired = await factory.HandleCommandAsync(Message("undoCompletion",
            $$"""{"undoToken":"{{token}}"}"""), default);
        Assert.Equal("TODO_CONFLICT", expired.ErrorCode);
        Assert.Equal(TodoStatus.Completed, service.Get(first.Id)!.Status);
    }

    [Fact]
    public async Task Narrow_workspace_can_move_from_navigation_to_list_to_detail()
    {
        await StaRunner.RunAsync(async () =>
        {
            var service = new TodoApplicationService(new Repository(), TimeProvider.System);
            service.Create("inbox item", null, TodoPriority.Normal, null);
            using var feed = new TodoChangeFeed(service);
            var factory = new TodoWorkspaceWebFactory(new TodoWebAdapter(service, feed), feed,
                Path.Combine(Path.GetTempPath(), "fgopet-narrow-" + Guid.NewGuid().ToString("N")));
            var surface = factory.CreateView();
            var window = new Window { Content = surface, Width = 380, Height = 580,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Show();
            await WaitUntil(() => surface.Host.State == WebSurfaceState.Ready);
            var web = Assert.Single(surface.Host.Children.OfType<WebView2>());
            await WaitUntil(async () => (await Script(web, "document.querySelector('#workspace-tasks').textContent")).Contains("inbox item", StringComparison.Ordinal));
            Assert.Equal("none", await Script(web, "getComputedStyle(document.querySelector('.workspace-tabs')).display"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#back-to-navigation').click()");
            Assert.Equal("navigation", await Script(web, "document.body.dataset.mobileStage"));
            Assert.NotEqual("none", await Script(web, "getComputedStyle(document.querySelector('.workspace-tabs')).display"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#inbox-tab').click()");
            Assert.Equal("list", await Script(web, "document.body.dataset.mobileStage"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.task-heading button').click()");
            Assert.Equal("detail", await Script(web, "document.body.dataset.mobileStage"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#back-to-list').click()");
            Assert.Equal("list", await Script(web, "document.body.dataset.mobileStage"));
            window.Close();
            await WaitUntil(() => surface.Host.State == WebSurfaceState.Closed);
        });
    }

    private static WebSurfaceMessage Message(string type, string json) =>
        new(type, "test", JsonDocument.Parse(json).RootElement.Clone());

    private static async Task<string> Script(WebView2 web, string code) =>
        JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(code)) ?? string.Empty;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition());
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition());
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

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
