using System.Runtime.ExceptionServices;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FgoPet.App.Services;
using FgoPet.App.Views;
using FgoPet.Core.Agents;
using FgoPet.Core.Archives;
using FgoPet.Core.Todo;
using Xunit;

namespace FgoPet.Windows.Tests.Todo;

[Trait("Category", "WindowsIntegration")]
public sealed class TodoWorkspaceViewIntegrationTests
{
    [Fact]
    public void Quick_add_keeps_title_visible_and_reveals_optional_description_on_demand()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                Assert.Equal(Visibility.Visible, view.FindName("QuickAddEditor") is FrameworkElement editor ? editor.Visibility : Visibility.Collapsed);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);

                Click(view, "添加说明与步骤");

                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);
                Assert.True(Assert.IsType<TextBox>(view.FindName("TitleInput")).IsVisible);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Cancelling_quick_add_clears_the_draft_without_writing()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                title.Text = "Draft";
                Click(view, "添加说明与步骤");
                description.Text = "Private draft details";

                Click(view, "取消");

                Assert.Empty(repository.Items);
                Assert.Equal(string.Empty, title.Text);
                Assert.Equal(string.Empty, description.Text);
                Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);
                Assert.True(title.IsVisible);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Failed_save_keeps_the_entire_draft_and_editor_visible()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository { SaveFailure = new IOException("simulated") };
            var view = ShowView(repository);
            try
            {
                var title = Assert.IsType<TextBox>(view.FindName("TitleInput"));
                var description = Assert.IsType<TextBox>(view.FindName("DescriptionInput"));
                title.Text = "Keep me";
                Click(view, "添加说明与步骤");
                description.Text = "1. Keep every step";

                Click(view, "保存");

                Assert.Equal("Keep me", title.Text);
                Assert.Equal("1. Keep every step", description.Text);
                Assert.True(title.IsVisible);
                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("OptionalDescriptionPanel")).Visibility);
                Assert.Contains("输入已保留", Assert.IsType<TextBlock>(view.FindName("StatusText")).Text);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Repeated_save_activation_creates_only_one_todo()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var view = ShowView(repository);
            try
            {
                Assert.IsType<TextBox>(view.FindName("TitleInput")).Text = "Only once";
                var save = FindVisualChildren<Button>(view).Single(button => Equals(button.Content, "保存"));

                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Single(repository.Items);
                Assert.Equal(1, repository.SaveCount);
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Todo_row_starts_compact_and_keeps_details_and_actions_in_its_expansion()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            repository.Save(Item("todo-1", "A long title that must wrap instead of overflowing", "Readable details"));
            var view = ShowView(repository);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = FindVisualChildren<Expander>(rows).Single();
                var title = FindVisualChildren<TextBlock>(row).Single(text => text.Text.StartsWith("A long title"));

                Assert.False(row.IsExpanded);
                Assert.Equal(TextWrapping.Wrap, title.TextWrapping);

                row.IsExpanded = true;
                view.UpdateLayout();

                Assert.Contains(FindVisualChildren<TextBlock>(row), text => text.Text == "Readable details" && text.TextWrapping == TextWrapping.Wrap);
                Assert.Contains(FindVisualChildren<Button>(row), button => Equals(button.Content, "复制任务说明"));
                Assert.Contains(FindVisualChildren<Button>(row), button => Equals(button.Content, "编辑"));
                Assert.Contains(FindVisualChildren<Button>(row), button => Equals(button.Content, "删除"));
            }
            finally { CloseView(view); }
        });
    }

    [Fact]
    public void Unknown_execution_remains_visible_and_blocks_mutating_row_actions()
    {
        StaRun(() =>
        {
            var repository = new FakeTodoRepository();
            var todo = Item("todo-protected", "Protected work", "Check the original task");
            repository.Save(todo);
            var agents = new FakeAgentRepository(new AgentExecution(
                "execution-1", todo.Id, "codex", "source-1", "task-1", "dispatch-1",
                DateTimeOffset.UtcNow, AgentExecutionStatus.DispatchOutcomeUnknown));
            var view = ShowView(repository, agents);
            try
            {
                var rows = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                var row = FindVisualChildren<Expander>(rows).Single();
                row.IsExpanded = true;
                view.UpdateLayout();

                Assert.False(FindVisualChildren<CheckBox>(rows).Single().IsEnabled);
                Assert.False(FindVisualChildren<Button>(row).Single(button => Equals(button.Content, "编辑")).IsEnabled);
                Assert.False(FindVisualChildren<Button>(row).Single(button => Equals(button.Content, "删除")).IsEnabled);
                Assert.Contains(FindVisualChildren<TextBlock>(row), text => text.Text == "待核对");
                Assert.DoesNotContain(FindVisualChildren<Expander>(view), expander => Equals(expander.Header, "Agent 兼容记录"));
                Assert.Contains(FindVisualChildren<Expander>(view), expander => Equals(expander.Header, "执行记录与恢复"));
            }
            finally { CloseView(view); }
        });
    }

    private static TodoWorkspaceView ShowView(FakeTodoRepository repository, IAgentRepository? agents = null)
    {
        var view = new TodoWorkspaceView(new TodoApplicationService(repository, TimeProvider.System, agents), agents);
        var window = new Window { Width = 720, Height = 520, Content = view };
        window.Show();
        view.Refresh();
        view.UpdateLayout();
        return view;
    }

    private static void CloseView(TodoWorkspaceView view)
    {
        var window = Window.GetWindow(view);
        window?.Close();
    }

    private static TodoItem Item(string id, string title, string? description) =>
        new(id, title, description, TodoPriority.Normal, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static void Click(DependencyObject root, string content) =>
        FindVisualChildren<Button>(root).Single(button => Equals(button.Content, content))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeTodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = new();
        public int SaveCount { get; private set; }
        public Exception? SaveFailure { get; init; }

        public void Save(TodoItem todo)
        {
            if (SaveFailure is not null) throw SaveFailure;
            Items.RemoveAll(item => item.Id == todo.Id);
            Items.Add(todo);
            SaveCount++;
        }

        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) =>
            Items.Where(item => status is null || item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) =>
            Items.Where(item => item.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date).ToArray();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }

    private sealed class FakeAgentRepository(AgentExecution execution) : IAgentRepository
    {
        public void SaveExecution(AgentExecution value) => throw new NotSupportedException();
        public AgentExecution? GetExecution(string id) => execution.Id == id ? execution : null;
        public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId) => execution;
        public AgentExecution? GetLatestExecutionForTodo(string todoId) => execution.TodoId == todoId ? execution : null;
        public IReadOnlyList<AgentExecution> ListNonTerminalExecutions() => [execution];
        public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit) => [];
        public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence) => false;
        public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent) => throw new NotSupportedException();
        public void SaveArchiveBatch(AgentArchiveBatch batch) { }
        public AgentArchiveBatch? GetArchiveBatch(string batchId) => null;
        public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches() => [];
        public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt) { }
        public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets) { }
        public IReadOnlyList<PersistedAgentConnection> ListConnections() => [];
    }
}
