using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;

namespace FgoPet.Windows.Tests.Dialogue;

[Trait("Category", "WindowsIntegration")]
public sealed class DialogueWindowIntegrationTests
{
    [Fact]
    public void More_menu_groups_settings_by_user_goal()
    {
        StaRun(() =>
        {
            var settings = new FgoPet.App.Settings.SettingsViewModel();
            var window = new DialogueWindow(CreateViewModel(), settingsNavigation: settings);
            try
            {
                var more = Assert.IsType<Button>(FindField(window, "MoreButton"));
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = Assert.IsType<ContextMenu>(window.LastMoreMenu);
                var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                Assert.Equal(settings.NavigationGroups.Select(group => group.Label), headers);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Close_hides_instead_of_closing_so_the_window_reuses_the_session()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                Assert.True(window.IsVisible);

                window.Close();

                Assert.False(window.IsVisible);

                window.Show();
                Assert.True(window.IsVisible);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Open_request_shows_and_activates_the_window()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                viewModel.NotifyWindowHidden();
                viewModel.RequestOpen();

                Assert.True(window.IsVisible);
                Assert.Equal(0, viewModel.UnreadCount);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Unread_counts_while_hidden_and_clears_on_open()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                viewModel.RequestOpen();
                window.Hide();

                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel("m1", Core.Dialogue.ChatMessageRole.Assistant, "回复"));

                Assert.Equal(1, viewModel.UnreadCount);

                viewModel.RequestOpen();

                Assert.Equal(0, viewModel.UnreadCount);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Turns_added_while_following_scroll_kept_the_bottom()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                window.Show();
                for (var index = 1; index <= 12; index++)
                {
                    viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                        $"m{index}", Core.Dialogue.ChatMessageRole.Assistant, $"消息 {index}"));
                }

                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var scroller = FindField(window, "MessageScroller") as System.Windows.Controls.ScrollViewer;
                Assert.NotNull(scroller);
                Assert.True(scroller!.ScrollableHeight <= 0.5 || scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Assistant_bubble_is_left_aligned_and_user_bubble_is_right_aligned()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                window.Show();
                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                    "assistant", ChatMessageRole.Assistant, "从者消息"));
                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                    "user", ChatMessageRole.User, "用户消息"));

                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var scroller = Assert.IsType<ScrollViewer>(FindField(window, "MessageScroller"));
                var assistantText = FindVisualChildren<TextBlock>(window).Single(text => text.Text == "从者消息");
                var userText = FindVisualChildren<TextBlock>(window).Single(text => text.Text == "用户消息");
                var assistantBubble = Assert.IsType<Border>(VisualTreeHelper.GetParent(assistantText));
                var userBubble = Assert.IsType<Border>(VisualTreeHelper.GetParent(userText));
                var scrollerOrigin = scroller.TransformToAncestor(window).Transform(new Point(0, 0));
                var assistantOrigin = assistantBubble.TransformToAncestor(window).Transform(new Point(0, 0));
                var userOrigin = userBubble.TransformToAncestor(window).Transform(new Point(0, 0));

                Assert.InRange(assistantOrigin.X - scrollerOrigin.X, 0, 48);
                Assert.InRange(
                    scrollerOrigin.X + scroller.ActualWidth - (userOrigin.X + userBubble.ActualWidth),
                    0,
                    48);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Service_registration_uses_one_dialogue_window_instance()
    {
        StaRun(() =>
        {
            using var provider = FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(
                new Microsoft.Extensions.DependencyInjection.ServiceCollection(), []).BuildServiceProvider();

            var first = provider.GetRequiredService<DialogueWindow>();
            var second = provider.GetRequiredService<DialogueWindow>();
            var firstViewModel = provider.GetRequiredService<DialogueWindowViewModel>();
            var secondViewModel = provider.GetRequiredService<DialogueWindowViewModel>();

            Assert.Same(first, second);
            Assert.Same(firstViewModel, secondViewModel);
            Assert.Same(firstViewModel.Conversation, secondViewModel.Conversation);
            first.Close();
        });
    }

    [Fact]
    public void Dialogue_input_binds_enter_to_send_and_keeps_shift_enter_for_newline()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                var input = Assert.IsType<TextBox>(FindField(window, "InputBox"));
                var binding = Assert.Single(input.InputBindings.OfType<KeyBinding>());
                Assert.Equal(Key.Enter, binding.Key);
                Assert.Equal(ModifierKeys.None, binding.Modifiers);
                Assert.Equal("Composer.SendOrStopCommand", BindingOperations.GetBinding(binding, InputBinding.CommandProperty)?.Path.Path);
                Assert.True(input.AcceptsReturn);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    private static DialogueWindow CreateWindow() => new(CreateViewModel());

    [Fact]
    public void Reasoning_toggle_keeps_a_caption_when_the_provider_has_no_summary()
    {
        StaRun(() =>
        {
            var vm=CreateViewModel();
            var window=new DialogueWindow(vm);
            try
            {
                var turn=new ConversationTurnViewModel("reasoning",ChatMessageRole.Assistant,"回答");
                turn.AppendReasoning("测试供应商返回的思考片段");
                turn.IsReasoningExpanded=false;
                vm.Conversation.Turns.Add(turn);
                window.Show();
                window.Dispatcher.Invoke(() => {},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var toggle=FindVisualChildren<System.Windows.Controls.Primitives.ToggleButton>(window).Single(button => button.Name=="ReasoningToggle");
                Assert.True(toggle.IsVisible);
                Assert.Contains(FindVisualChildren<TextBlock>(toggle),text => text.Text=="思考");
                toggle.IsChecked=true;
                window.UpdateLayout();
                Assert.True(turn.IsReasoningExpanded);
                Assert.True(FindVisualChildren<TextBlock>(window).Single(text => text.Text=="测试供应商返回的思考片段").IsVisible);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Settings_request_is_forwarded_without_replacing_chat_content()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var window = new DialogueWindow(vm);
            FgoPet.App.Settings.SettingsSection? requested = null;
            vm.SettingsRequested += section => requested = section;
            try
            {
                vm.NavigateToSettings(FgoPet.App.Settings.SettingsSection.Personalization);

                Assert.Equal(FgoPet.App.Settings.SettingsSection.Personalization, requested);
                Assert.Equal(MainNavigationTarget.Companion, vm.CurrentTarget);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ConversationPage")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(FindField(window, "ContextPage")).Visibility);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Legacy_settings_targets_are_forwarded_without_entering_dialogue_context()
    {
        var vm = CreateViewModel();
        FgoPet.App.Settings.SettingsSection? requested = null;
        vm.SettingsRequested += section => requested = section;

        vm.NavigateTo(MainNavigationTarget.Servant);
        Assert.Equal(FgoPet.App.Settings.SettingsSection.RolePackages, requested);
        Assert.Equal(MainNavigationTarget.Companion, vm.CurrentTarget);

        vm.NavigateTo(MainNavigationTarget.MemoryReview);
        Assert.Equal(FgoPet.App.Settings.SettingsSection.ConversationMemory, requested);
        Assert.Equal(MainNavigationTarget.Companion, vm.CurrentTarget);

        vm.NavigateTo(MainNavigationTarget.AgentReconciliation);
        Assert.Equal(FgoPet.App.Settings.SettingsSection.AgentConnection, requested);
        Assert.Equal(MainNavigationTarget.Companion, vm.CurrentTarget);

        vm.NavigateTo(MainNavigationTarget.TodoDetail, selectedId: "todo-1");
        Assert.Equal(MainNavigationTarget.Schedule, vm.CurrentTarget);
    }

    [Fact]
    public void Navigation_replaces_chat_surface_and_preserves_the_draft()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var window = new DialogueWindow(vm);
            try
            {
                var input = Assert.IsType<TextBox>(FindField(window, "InputBox"));
                input.Text = "尚未发送的草稿";
                vm.NavigateTo(MainNavigationTarget.Schedule);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ConversationPage")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ContextPage")).Visibility);
                Assert.Equal(300, Assert.IsType<Grid>(FindField(window, "ContextPage")).Width);
                vm.NavigateTo(MainNavigationTarget.Companion);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ConversationPage")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(FindField(window, "ContextPage")).Visibility);
                Assert.Equal("尚未发送的草稿", input.Text);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Configuration_request_stays_in_chat_and_preserves_draft()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var window = new DialogueWindow(vm);
            FgoPet.App.Settings.SettingsSection? requested = null;
            vm.SettingsRequested += section => requested = section;
            try
            {
                window.Show();
                var input = Assert.IsType<TextBox>(FindField(window, "InputBox"));
                input.Text = "打开设置后的草稿";

                vm.NavigateToSettings(FgoPet.App.Settings.SettingsSection.ModelConnection);
                window.UpdateLayout();

                Assert.Equal(FgoPet.App.Settings.SettingsSection.ModelConnection, requested);
                Assert.Equal(MainNavigationTarget.Companion, vm.CurrentTarget);
                Assert.Equal("打开设置后的草稿", input.Text);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Settings_request_does_not_add_dialogue_navigation_history()
    {
        var vm = CreateViewModel();

        vm.NavigateToSettings(FgoPet.App.Settings.SettingsSection.ModelConnection);

        Assert.Equal(MainNavigationTarget.Companion, vm.CurrentTarget);
        Assert.False(vm.NavigateBack());
    }

    [Fact]
    public void Portrait_treatment_reduces_at_narrow_and_constrained_widths()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                var avatar = Assert.IsType<System.Windows.Controls.Image>(FindField(window, "ServantAvatar"));

                window.Width = 800;
                window.UpdateLayout();
                Assert.Equal(24, avatar.Width);

                window.Width = 640;
                window.UpdateLayout();
                Assert.Equal(20, avatar.Width);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Narrow_history_overlays_without_compressing_the_conversation()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Width = 640;
                window.Show();
                var button = Assert.IsType<Button>(FindField(window, "HistoryDrawerButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var pane = Assert.IsType<Border>(FindField(window, "HistoryPane"));
                Assert.Equal(Visibility.Visible, pane.Visibility);
                Assert.Equal(2, Grid.GetColumnSpan(pane));
                Assert.Equal(0, Assert.IsType<ColumnDefinition>(FindField(window, "SidebarColumn")).ActualWidth);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, pane.Visibility);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    private static DialogueWindowViewModel CreateViewModel()
    {
        var settingsStore = new FakeSettingsStore(
            FgoPet.Core.Settings.AppSettings.Defaults with
            {
                ModelConnection = new FgoPet.Core.Settings.ModelConnectionSettings(
                    "test", "https://example.test/v1", "test-model"),
            });
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            new FgoPet.Infrastructure.Dialogue.SqliteConversationRepository(
                new FgoPet.Infrastructure.Persistence.RuntimeDatabase(":memory:")),
            new FgoPet.Infrastructure.Memory.SqliteMemoryRepository(
                new FgoPet.Infrastructure.Persistence.RuntimeDatabase(":memory:")),
            new PromptComposer(),
            TimeProvider.System,
            settingsStore);
        return new DialogueWindowViewModel(new ConversationViewModel(orchestrator, settingsStore));
    }

    private static DependencyObject? FindField(DialogueWindow window, string name) =>
        window.FindName(name) as DependencyObject;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class FakeSettingsStore(FgoPet.Core.Settings.AppSettings initial) : FgoPet.Core.Settings.IAppSettingsStore
    {
        public string Location => "memory";
        public FgoPet.Core.Settings.AppSettings Load() => initial;
        public void Save(FgoPet.Core.Settings.AppSettings settings) { }
    }

    private sealed class TestCredentials : FgoPet.Infrastructure.Secrets.ICredentialStore, FgoPet.Infrastructure.Secrets.ICredentialReader
    {
        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task DeleteAsync(string target, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
    private sealed class ThrowingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
                FgoPet.Infrastructure.Providers.ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
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
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
