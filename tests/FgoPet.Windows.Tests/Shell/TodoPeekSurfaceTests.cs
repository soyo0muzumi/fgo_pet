using System.IO;
using System.Text.Json;
using System.Windows;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.App.Windowing;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using FgoPet.Plugin.Todo.Desktop;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class TodoPeekSurfaceTests
{
    [Fact]
    public async Task Peek_page_quick_add_and_confirmed_changes_share_the_authoritative_service()
    {
        await StaRunner.RunAsync(async () =>
        {
            var service = new TodoApplicationService(new Repository(), TimeProvider.System);
            using var feed = new TodoChangeFeed(service);
            var adapter = new TodoWebAdapter(service, feed);
            string? openedWorkspace = null;
            using var factory = new TodoPeekSurfaceFactory(adapter, feed, TimeProvider.System,
                id => { openedWorkspace = id; return true; },
                Path.Combine(Path.GetTempPath(), "fgopet-peek-" + Guid.NewGuid().ToString("N")));
            var host = Assert.IsType<WebView2SurfaceHost>(factory.CreateView());
            Assert.Equal(1, feed.ActiveSubscriptionCount);
            var owner = new Window { Width = 320, Height = 400, ShowInTaskbar = false,
                Left = -10000, Top = -10000 };
            owner.Show();
            var window = new TransientSurfaceWindow(owner,
                new TransientSurfaceDescriptor("todo.peek", "今日待办", 320, 400), host)
            { Left = -10000, Top = -10000 };
            window.Show();
            var web = Assert.Single(host.Children.OfType<WebView2>());
            await WaitUntil(() => host.State != WebSurfaceState.Initializing);
            Assert.Equal(WebSurfaceState.Ready, host.State);
            host.SetThemeVersion(1, new Dictionary<string, string> { ["--accent"] = "#123456" });
            await WaitUntil(() => host.AckedThemeVersion == 1);

            await web.CoreWebView2.ExecuteScriptAsync(
                "document.getElementById('title').value='from peek'; document.getElementById('quick-add').requestSubmit();");
            await WaitUntil(() => service.ListActive().Any(item => item.Title == "from peek"));
            var created = service.ListActive().Single(item => item.Title == "from peek");
            await WaitUntil(async () => (await ReadPageText(web)).Contains("from peek", StringComparison.Ordinal));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#tasks input[type=checkbox]').click()");
            await WaitUntil(() => service.Get(created.Id)?.Status == TodoStatus.Completed);

            var proposals = new TodoProposalService(service);
            proposals.Confirm(Assert.Single(proposals.Parse("""{"todos":[{"title":"confirmed suggestion"}]}""")));
            await WaitUntil(async () => (await ReadPageText(web)).Contains("confirmed suggestion", StringComparison.Ordinal));
            Assert.Single(service.ListActive());
            await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('manage-all').click()");
            await WaitUntil(() => openedWorkspace == "todo.workspace");

            window.Close();
            Assert.Equal(WebSurfaceState.Closed, host.State);
            Assert.Equal(0, feed.ActiveSubscriptionCount);
            var reopened = Assert.IsType<WebView2SurfaceHost>(factory.CreateView());
            Assert.Equal(1, feed.ActiveSubscriptionCount);
            var next = new TransientSurfaceWindow(owner,
                new TransientSurfaceDescriptor("todo.peek", "今日待办", 320, 400), reopened)
            { Left = -10000, Top = -10000 };
            next.Show();
            await WaitUntil(() => reopened.State != WebSurfaceState.Initializing);
            Assert.Equal(WebSurfaceState.Ready, reopened.State);
            next.Close();
            Assert.Equal(WebSurfaceState.Closed, reopened.State);
            Assert.Equal(0, feed.ActiveSubscriptionCount);
            owner.Close();
        });
    }

    [Fact]
    public async Task Typed_commands_validate_input_and_keep_step_confirmation()
    {
        var service = new TodoApplicationService(new Repository(), TimeProvider.System);
        using var feed = new TodoChangeFeed(service);
        string? requestedWorkspace = null;
        using var factory = new TodoPeekSurfaceFactory(new TodoWebAdapter(service, feed), feed,
            TimeProvider.System, id => { requestedWorkspace = id; return true; }, Path.GetTempPath());
        var invalid = await factory.HandleCommandAsync(Message("quickAdd", """{"title":" "}"""), default);
        Assert.Equal("TODO_INVALID_INPUT", invalid.ErrorCode);
        var item = service.Create("with step", null, TodoPriority.Normal, null, ["do this"]);
        var etag = new TodoWebAdapter(service, feed).GetTask(item.Id)!.Etag;
        var blocked = await factory.HandleCommandAsync(Message("setCompletion",
            $$"""{"id":"{{item.Id}}","etag":"{{etag}}","completed":true}"""), default);
        Assert.Equal("TODO_CONFIRM_INCOMPLETE_STEPS", blocked.ErrorCode);
        Assert.Equal(TodoStatus.Planned, service.Get(item.Id)!.Status);
        var open = await factory.HandleCommandAsync(Message("openWorkspace", "{}"), default);
        Assert.True(open.Success);
        Assert.Equal("todo.workspace", requestedWorkspace);
    }

    [Fact]
    public async Task Incomplete_steps_require_an_inline_confirmation_before_completion()
    {
        await StaRunner.RunAsync(async () =>
        {
            var service = new TodoApplicationService(new Repository(), TimeProvider.System);
            var item = service.Create("needs confirmation", null, TodoPriority.Normal, null, ["step"]);
            using var feed = new TodoChangeFeed(service);
            using var factory = new TodoPeekSurfaceFactory(new TodoWebAdapter(service, feed), feed,
                TimeProvider.System, _ => false,
                Path.Combine(Path.GetTempPath(), "fgopet-confirm-" + Guid.NewGuid().ToString("N")));
            var host = Assert.IsType<WebView2SurfaceHost>(factory.CreateView());
            var owner = new Window { Width = 320, Height = 400, ShowInTaskbar = false,
                Left = -10000, Top = -10000 };
            owner.Show();
            var window = new TransientSurfaceWindow(owner,
                new TransientSurfaceDescriptor("todo.peek", "今日待办", 320, 400), host)
            { Left = -10000, Top = -10000 };
            window.Show();
            await WaitUntil(() => host.State == WebSurfaceState.Ready);
            var web = Assert.Single(host.Children.OfType<WebView2>());
            await WaitUntil(async () => (await ReadPageText(web)).Contains(item.Title, StringComparison.Ordinal));

            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#tasks input[type=checkbox]').click()");
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('.confirmation') !== null") == "true");
            Assert.Equal(TodoStatus.Planned, service.Get(item.Id)!.Status);
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.confirmation button').click()");
            Assert.Equal("false", await web.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('.confirmation') !== null"));
            Assert.Equal(TodoStatus.Planned, service.Get(item.Id)!.Status);

            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#tasks input[type=checkbox]').click()");
            await WaitUntil(async () => await web.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('.confirmation') !== null") == "true");
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.confirmation button:last-child').click()");
            await WaitUntil(() => service.Get(item.Id)?.Status == TodoStatus.Completed);
            window.Close();
            owner.Close();
        });
    }

    private static WebSurfaceMessage Message(string type, string json) =>
        new(type, "test", JsonDocument.Parse(json).RootElement.Clone());

    private static async Task<string> ReadPageText(WebView2 web)
    {
        var json = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('tasks').textContent");
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "The Peek page did not settle before the deadline.");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(await condition(), "The Peek page did not update before the deadline.");
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
