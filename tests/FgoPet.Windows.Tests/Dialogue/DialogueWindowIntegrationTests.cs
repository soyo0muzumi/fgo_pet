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
    public void Chat_header_names_the_two_content_views_instead_of_a_more_menu()
    {
        StaRun(() =>
        {
            var window = new DialogueWindow(CreateViewModel());
            try
            {
                // The grouped "more" menu was replaced by an explicit chat/todo switch.
                Assert.Null(FindField(window, "MoreButton"));
                Assert.Equal("新对话", Assert.IsType<Button>(FindField(window, "NewConversationButton")).Content);
                Assert.Equal("聊天", Assert.IsType<Button>(FindField(window, "ChatTabButton")).Content);
                Assert.Equal("待办", Assert.IsType<Button>(FindField(window, "TasksButton")).Content);
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

                StaRunner.Pump(window.Dispatcher);

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

                StaRunner.Pump(window.Dispatcher);

                var scroller = Assert.IsType<ScrollViewer>(FindField(window, "MessageScroller"));
                // Message text is rendered inside a read-only TextBox inside the bubble border.
                var assistantText = FindVisualChildren<TextBox>(window).Single(text => text.Text == "从者消息");
                var userText = FindVisualChildren<TextBox>(window).Single(text => text.Text == "用户消息");
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
    public void Confirmed_assistant_turn_exposes_only_a_small_view_todo_action()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var repository = new FeedbackTodoRepository();
            var created = new FgoPet.Core.Todo.TodoItem(
                "todo-confirmed", "确认后的待办", null,
                FgoPet.Core.Todo.TodoPriority.Normal, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            repository.Save(created);
            var service = new FgoPet.App.Services.TodoApplicationService(repository, TimeProvider.System);
            var turn = new ConversationTurnViewModel("assistant-confirmed", ChatMessageRole.Assistant, "已创建待办“确认后的待办”。");
            turn.CreatedTodoId = created.Id;
            viewModel.Conversation.Turns.Add(turn);
            var window = new DialogueWindow(viewModel, todoService: service);
            try
            {
                window.Show();
                window.UpdateLayout();

                var viewButton = Assert.Single(FindVisualChildren<Button>(window).Where(button =>
                    button.Tag is ConversationTurnViewModel
                    && System.Windows.Automation.AutomationProperties.GetName(button) == "查看待办"));
                Assert.Equal(Visibility.Visible, viewButton.Visibility);
                Assert.Equal("查看待办", viewButton.ToolTip);

                viewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                Assert.Equal(Visibility.Visible, Assert.IsType<ContentControl>(FindField(window, "TasksPage")).Visibility);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Copying_an_assistant_reply_keeps_the_chat_window_alive()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var turn = new ConversationTurnViewModel("assistant-copy", ChatMessageRole.Assistant, "可复制的回复");
            viewModel.Conversation.Turns.Add(turn);
            var window = new DialogueWindow(viewModel);
            try
            {
                window.Show();
                window.UpdateLayout();
                var copy = Assert.Single(FindVisualChildren<Button>(window).Where(button =>
                    button.Tag is ConversationTurnViewModel tagged
                    && ReferenceEquals(tagged, turn)
                    && System.Windows.Automation.AutomationProperties.GetName(button) == "复制"));

                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(window.IsVisible);
                Assert.Equal("已复制", System.Windows.Automation.AutomationProperties.GetName(copy));
                Assert.Equal("已复制", copy.ToolTip);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Empty_chat_keeps_character_art_in_the_header_and_exposes_only_focus_and_todo_shortcuts()
    {
        StaRun(() =>
        {
            var panel = new FgoPet.App.Panels.AttachedPanelViewModel(TimeProvider.System);
            var window = new DialogueWindow(CreateViewModel(), panel: panel);
            try
            {
                window.Show();
                window.UpdateLayout();

                var welcome = Assert.IsType<StackPanel>(FindField(window, "Welcome"));
                Assert.Empty(FindVisualChildren<Image>(welcome));
                Assert.Single(FindVisualChildren<Image>(window));
                var focus = Assert.IsType<Button>(FindField(window, "FocusShortcutButton"));
                var todo = Assert.IsType<Button>(FindField(window, "TodoShortcutButton"));
                Assert.Equal("开始专注", focus.Content);
                Assert.Equal("查看待办", todo.Content);

                focus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(FgoPet.Core.Panels.AttachedPanelState.ExpandedFocus, panel.State);

                window.Show();
                todo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(FindField(window, "ChatBody")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.IsType<ContentControl>(FindField(window, "TasksPage")).Visibility);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Welcome_leaves_the_chat_body_when_conversation_content_begins()
    {
        StaRun(() =>
        {
            var viewModel = CreateViewModel();
            var window = new DialogueWindow(viewModel);
            try
            {
                window.Show();
                Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(FindField(window, "Welcome")).Visibility);

                viewModel.Conversation.Turns.Add(new ConversationTurnViewModel(
                    "assistant", ChatMessageRole.Assistant, "欢迎之后的第一条消息"));

                Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(FindField(window, "Welcome")).Visibility);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Constrained_chat_hides_secondary_welcome_content_and_keeps_a_scrollable_composer()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                Assert.Equal(720, window.Width);
                Assert.Equal(640, window.Height);
                Assert.Equal(500, window.MinWidth);
                Assert.Equal(540, window.MinHeight);

                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.Show();
                window.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(FindField(window, "WelcomeSecondaryCopy")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(FindField(window, "WelcomeShortcuts")).Visibility);
                var composer = Assert.IsType<Border>(FindField(window, "ComposerBorder"));
                Assert.True(composer.ActualHeight >= 76, $"Composer height was {composer.ActualHeight:0.##} DIP.");
                var input = Assert.IsType<TextBox>(FindField(window, "InputBox"));
                Assert.Equal(ScrollBarVisibility.Auto, input.VerticalScrollBarVisibility);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Composer_border_uses_the_focus_ring_when_the_input_has_keyboard_focus()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/Themes/FgoLight.xaml", UriKind.Relative),
            });
            try
            {
                window.Show();
                var input = Assert.IsType<TextBox>(FindField(window, "InputBox"));
                var composer = Assert.IsType<Border>(FindField(window, "ComposerBorder"));
                var focusRing = Assert.IsType<SolidColorBrush>(window.FindResource("Focus.Ring"));
                var controlBorder = Assert.IsType<SolidColorBrush>(window.FindResource("Border.Control"));
                Assert.NotEqual(controlBorder.Color, focusRing.Color);

                input.Focus();
                Keyboard.Focus(input);
                StaRunner.Pump(window.Dispatcher);

                Assert.True(input.IsKeyboardFocusWithin);
                Assert.Equal(focusRing.Color, Assert.IsType<SolidColorBrush>(composer.BorderBrush).Color);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Wide_chat_keeps_the_reading_column_at_no_more_than_680_dip()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Width = 1000;
                window.Height = 720;
                window.Show();
                window.UpdateLayout();

                var readingColumn = Assert.IsType<StackPanel>(FindField(window, "ReadingColumn"));
                Assert.Equal(680, readingColumn.MaxWidth);
                Assert.InRange(readingColumn.ActualWidth, 0, 680);
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
    public void Enter_is_consumed_for_sending_and_the_text_box_keeps_multiline_input()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var window = new DialogueWindow(vm);
            try
            {
                window.Show();
                var input = Assert.IsType<TextBox>(FindField(window, "InputBox"));
                // Enter is handled centrally on the window (so the IME composing guard can
                // apply) rather than through a KeyBinding on the text box.
                Assert.Empty(input.InputBindings.OfType<KeyBinding>());
                Assert.True(input.AcceptsReturn, "Shift+Enter must be able to insert a newline");

                input.Focus();
                Keyboard.Focus(input);
                StaRunner.Pump(window.Dispatcher);
                Assert.True(input.IsKeyboardFocusWithin, "the composer must hold keyboard focus");

                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                window.RaiseEvent(enter);

                Assert.True(enter.Handled, "Enter must be consumed by the send path");
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    private static DialogueWindow CreateWindow() => new(CreateViewModel());

    [Fact]
    public void Reasoning_toggle_keeps_a_caption_and_reveals_the_reasoning_text()
    {
        StaRun(() =>
        {
            var vm=CreateViewModel();
            var window=new DialogueWindow(vm);
            try
            {
                var turn=new ConversationTurnViewModel("reasoning",ChatMessageRole.Assistant,"回答");
                turn.AppendReasoning("The model is reasoning");
                turn.IsReasoningExpanded=false;
                vm.Conversation.Turns.Add(turn);
                window.Show();
                StaRunner.Pump(window.Dispatcher);

                var well=FindVisualChildren<Border>(window).Single(border => border.Name=="ReasoningWell");
                Assert.True(well.IsVisible);

                var toggle=FindVisualChildren<System.Windows.Controls.Primitives.ToggleButton>(window)
                    .Single(button => button.Name=="ReasoningToggle");
                Assert.True(toggle.IsVisible);
                Assert.Contains(FindVisualChildren<TextBlock>(toggle),text => text.Text=="思考");

                var reasoningText=FindVisualChildren<TextBlock>(window)
                    .Single(text => text.Text=="The model is reasoning");
                Assert.False(reasoningText.IsVisible);

                toggle.IsChecked=true;
                window.UpdateLayout();
                Assert.True(turn.IsReasoningExpanded);
                Assert.True(reasoningText.IsVisible);

                // Streaming fragments must not be trimmed: English words keep their spaces.
                Assert.Equal("The model is reasoning",turn.ReasoningText);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Empty_assistant_turn_does_not_render_a_bubble_or_message_actions()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var window = new DialogueWindow(vm);
            try
            {
                vm.Conversation.Turns.Add(new ConversationTurnViewModel("tool", ChatMessageRole.Assistant, string.Empty));
                window.Show();
                StaRunner.Pump(window.Dispatcher);

                var body = FindVisualChildren<Border>(window).Single(border => border.Name == "MessageBody");
                var actions = FindVisualChildren<StackPanel>(window).Single(panel => panel.Name == "MessageActions");
                Assert.False(body.IsVisible);
                Assert.False(actions.IsVisible);
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
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ChatBody")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<ContentControl>(FindField(window, "TasksPage")).Visibility);
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
                // The todo view replaces the chat body inside the same window.
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(FindField(window, "ChatBody")).Visibility);
                Assert.Equal(Visibility.Visible, Assert.IsType<ContentControl>(FindField(window, "TasksPage")).Visibility);
                vm.NavigateTo(MainNavigationTarget.Companion);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ChatBody")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<ContentControl>(FindField(window, "TasksPage")).Visibility);
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
    public void The_header_keeps_one_fixed_role_chip_at_narrow_and_regular_widths()
    {
        StaRun(() =>
        {
            var window = CreateWindow();
            try
            {
                window.Show();
                // The header no longer scales a large portrait; it shows a single 30 DIP chip.
                Assert.Equal(500, window.MinWidth);

                window.Width = 800;
                window.UpdateLayout();
                Assert.Equal(30, FindVisualChildren<Border>(window).First(border => border.Width == 30 && border.Height == 30).Width);

                window.Width = 640;
                window.UpdateLayout();
                Assert.Equal(30, FindVisualChildren<Border>(window).First(border => border.Width == 30 && border.Height == 30).Width);
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
                var button = Assert.IsType<Button>(FindField(window, "HistoryButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var drawer = Assert.IsType<Border>(FindField(window, "HistoryDrawer"));
                Assert.Equal(Visibility.Visible, drawer.Visibility);
                // The drawer overlays the chat body instead of compressing it.
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(FindField(window, "ChatBody")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)FindField(window, "Backdrop")!).Visibility);

                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                window.RaiseEvent(escape);

                Assert.True(escape.Handled, "Escape must close the overlay");
                Assert.Equal(Visibility.Collapsed, drawer.Visibility);
            }
            finally { window.Dispatcher.InvokeShutdown(); }
        });
    }

    [Fact]
    public void Todo_proposal_card_shows_summary_until_editor_is_expanded()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var repository = new FeedbackTodoRepository();
            var todoService = new FgoPet.App.Services.TodoApplicationService(repository, TimeProvider.System);
            var proposal = new FgoPet.App.ViewModels.TodoProposalViewModel(
                new TodoProposal("整理学习计划", "先列出目标，再安排复习顺序。"),
                new TodoProposalService(todoService));
            vm.Conversation.TodoProposals.Add(proposal);
            var window = new DialogueWindow(vm, todoService: todoService);
            try
            {
                window.Show();
                window.UpdateLayout();

                var card = Assert.Single(FindVisualChildren<FgoPet.App.Views.TodoProposalCard>(window));
                var editor = Assert.Single(FindVisualChildren<Expander>(card));
                Assert.False(editor.IsExpanded);
                Assert.Contains(FindVisualChildren<TextBlock>(card), text => text.Text == "先列出目标，再安排复习顺序。");
                Assert.All(FindVisualChildren<TextBox>(card), text => Assert.False(text.IsVisible));
                Assert.Contains(FindVisualChildren<Button>(card), button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "加入待办");
                Assert.Contains(FindVisualChildren<Button>(card), button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "忽略建议");

                editor.IsExpanded = true;
                window.UpdateLayout();
                Assert.True(editor.IsExpanded);
                Assert.Equal(2, FindVisualChildren<TextBox>(card).Count(text => text.IsVisible));
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Fact]
    public void Confirmed_todo_proposal_shows_id_safe_success_and_view_entry()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var repository = new FeedbackTodoRepository();
            var todoService = new FgoPet.App.Services.TodoApplicationService(repository, TimeProvider.System);
            var proposal = new FgoPet.App.ViewModels.TodoProposalViewModel(
                new TodoProposal("查看入口目标", "保留这条备注。"),
                new TodoProposalService(todoService));
            vm.Conversation.TodoProposals.Add(proposal);
            var window = new DialogueWindow(vm, todoService: todoService);
            try
            {
                window.Show();
                window.UpdateLayout();
                var card = Assert.Single(FindVisualChildren<FgoPet.App.Views.TodoProposalCard>(window));
                var add = FindVisualChildren<Button>(card).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "加入待办");
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();

                var createdId = Assert.IsType<string>(proposal.CreatedTodoId);
                Assert.True(proposal.IsAdded);
                Assert.Single(repository.List());
                Assert.Equal(createdId, repository.List().Single().Id);
                Assert.Contains(FindVisualChildren<TextBlock>(card), text => text.Text == "已加入：");
                Assert.Contains(FindVisualChildren<TextBlock>(card), text => text.Text == "查看入口目标");

                var view = FindVisualChildren<Button>(card).Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "查看待办");
                view.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, ((ContentControl)window.FindName("TasksPage")!).Visibility);
                var workspace = Assert.IsType<FgoPet.App.Views.TodoWorkspaceView>(((ContentControl)window.FindName("TasksPage")!).Content);
                Assert.Single(((ItemsControl)workspace.FindName("ActiveItems")!).Items);
            }
            finally
            {
                window.Dispatcher.InvokeShutdown();
            }
        });
    }

    [Theory]
    [InlineData("FgoLight.xaml")]
    [InlineData("ModernGray.xaml")]
    public void Feedback_workspace_creates_todo_and_preserves_chat_draft(string theme)
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            vm.Conversation.InputText = "保留我的聊天草稿";
            var repository = new FeedbackTodoRepository();
            var service = new FgoPet.App.Services.TodoApplicationService(repository, TimeProvider.System);
            var window = new DialogueWindow(vm, todoService: service);
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/FgoPet.App;component/Themes/" + theme, UriKind.Relative) });
            try
            {
                window.Show();
                ((Button)window.FindName("TasksButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, ((Grid)window.FindName("ChatBody")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("Backdrop")).Visibility);
                var view = Assert.IsType<FgoPet.App.Views.TodoWorkspaceView>(((ContentControl)window.FindName("TasksPage")).Content);
                view.BeginAdd();
                ((TextBox)view.FindName("TitleInput")).Text = "学习 Transformer 架构";
                ((TextBox)view.FindName("DescriptionInput")).Text = "1. 理解自注意力\n2. 理解多头注意力与位置编码\n3. 阅读一份实现并总结";
                var save = FindVisualChildren<Button>(view).Single(b => Equals(b.Content, "保存"));
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.Refresh();
                window.UpdateLayout();
                Assert.Single(repository.List());
                Assert.Single(((ItemsControl)view.FindName("ActiveItems")).Items);
                var output = Environment.GetEnvironmentVariable("FGO_FEEDBACK_RENDER_DIR");
                if (!string.IsNullOrEmpty(output))
                {
                    System.IO.Directory.CreateDirectory(output);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(output, theme + ".png"));
                    encoder.Save(file);
                }
                ((Button)window.FindName("ChatTabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("保留我的聊天草稿", vm.Conversation.InputText);
                Assert.Equal(Visibility.Visible, ((Grid)window.FindName("ChatBody")).Visibility);
            }
            finally { window.Hide(); }
        });
    }

    [Fact]
    public void Reentering_todo_tab_regroups_completed_rows_without_restarting_the_window()
    {
        StaRun(() =>
        {
            var vm = CreateViewModel();
            var repository = new FeedbackTodoRepository();
            repository.Save(new FgoPet.Core.Todo.TodoItem(
                "todo-reenter", "重新进入后应归档", null,
                FgoPet.Core.Todo.TodoPriority.Normal, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            var service = new FgoPet.App.Services.TodoApplicationService(repository, TimeProvider.System);
            var window = new DialogueWindow(vm, todoService: service);
            try
            {
                window.Show();
                ((Button)window.FindName("TasksButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var view = Assert.IsType<FgoPet.App.Views.TodoWorkspaceView>(
                    ((ContentControl)window.FindName("TasksPage")!).Content);
                var active = Assert.IsType<ItemsControl>(view.FindName("ActiveItems"));
                Assert.Single(active.Items);

                var checkbox = FindVisualChildren<CheckBox>(active).Single();
                checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                window.UpdateLayout();
                Assert.Single(active.Items);
                Assert.Empty(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")).Items);

                ((Button)window.FindName("ChatTabButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("TasksButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();

                Assert.Empty(active.Items);
                Assert.Single(Assert.IsType<ItemsControl>(view.FindName("CompletedItems")).Items);
            }
            finally { window.Hide(); }
        });
    }

    private sealed class FeedbackTodoRepository : FgoPet.Core.Todo.ITodoRepository
    {
        private readonly Dictionary<string, FgoPet.Core.Todo.TodoItem> _items = new();
        public void Save(FgoPet.Core.Todo.TodoItem item) => _items[item.Id] = item;
        public FgoPet.Core.Todo.TodoItem? Get(string id) => _items.GetValueOrDefault(id);
        public IReadOnlyList<FgoPet.Core.Todo.TodoItem> List(FgoPet.Core.Todo.TodoStatus? status = null) =>
            _items.Values.Where(x => status is null || x.Status == status).ToArray();
        public IReadOnlyList<FgoPet.Core.Todo.TodoItem> ListCompletedOn(DateOnly date) => Array.Empty<FgoPet.Core.Todo.TodoItem>();
        public void Delete(string id) => _items.Remove(id);
        public void ClearAgentTodoData() => _items.Clear();
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

    private static void StaRun(Action action) => StaRunner.Run(action);
}
