using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FgoPet.App.Settings;
using FgoPet.App.ViewModels;
using FgoPet.Core.Panels;

namespace FgoPet.App.Dialogue;

public interface IClipboardWriter
{
    void SetText(string text);
}

internal sealed class SystemClipboardWriter : IClipboardWriter
{
    public void SetText(string text) => Clipboard.SetText(text);
}

/// <summary>Chat presentation shell. Conversation and task state remain owned by their view models.</summary>
public partial class DialogueWindow : Window
{
    private readonly DialogueWindowViewModel _viewModel;
    private readonly TodoListViewModel? _todos;
    private readonly IAttachedPanelLauncher? _panel;
    private FgoPet.App.Views.TodoWorkspaceView? _workspace;
    private bool _showingTasks;
    private bool _composing;
    private bool _following = true;
    private Button? _drawerOrigin;
    private Button? _historyDeleteOrigin;
    private readonly IClipboardWriter _clipboard;
    private readonly Dictionary<Button, System.Windows.Threading.DispatcherTimer> _copyFeedback = new();
    internal ContextMenu? LastMoreMenu { get; private set; }
    public event Action? Hidden;

    public DialogueWindow(DialogueWindowViewModel viewModel, TodoListViewModel? todos = null,
        IAttachedPanelLauncher? panel = null, ISettingsNavigator? settingsNavigation = null,
        FgoPet.App.Services.TodoApplicationService? todoService = null,
        FgoPet.Core.Agents.IAgentRepository? agents = null,
        AgentCurrentTaskViewModel? currentTask = null,
        FgoPet.App.Portraits.PortraitController? portrait = null,
        IClipboardWriter? clipboard = null)
    {
        _viewModel = viewModel;
        _todos = todos;
        _panel = panel;
        _clipboard = clipboard ?? new SystemClipboardWriter();
        InitializeComponent();
        DataContext = viewModel;
        FocusShortcutButton.IsEnabled = panel is not null;
        FocusShortcutButton.ToolTip = panel is null ? "专注入口当前不可用" : "打开现有专注设置";
        FocusNavigationButton.IsEnabled = panel is not null;
        FocusNavigationButton.ToolTip = FocusShortcutButton.ToolTip;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += async (_, _) => await viewModel.EnsureRoleInfoAsync();
        if (todoService is not null)
        {
            _workspace = new FgoPet.App.Views.TodoWorkspaceView(todoService, agents, currentTask);
            TasksPage.Content = _workspace;
        }
        viewModel.Conversation.ManualTodoRequested += () => { ShowTasks(true); _workspace?.BeginAdd(); };
        AddHandler(FgoPet.App.Views.TodoProposalCard.ViewTodoRequestedEvent, new RoutedEventHandler((_, e) => { ShowTasks(true); if (e.OriginalSource is FgoPet.App.Views.TodoProposalCard { DataContext: TodoProposalViewModel proposal }) _workspace?.FocusTodo(proposal.CreatedTodoId); }));
        TextCompositionManager.AddPreviewTextInputStartHandler(InputBox, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(InputBox, (_, _) => _composing = false);
        viewModel.Conversation.ExpressionRequested += semantic =>
        {
            var state = portrait?.CurrentState;
            var conversationId = viewModel.Conversation.CurrentConversationId;
            var servant = viewModel.Conversation.ActiveServantId;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (state is null || portrait is null || !ReferenceEquals(portrait.CurrentState, state) ||
                    viewModel.Conversation.CurrentConversationId != conversationId ||
                    viewModel.Conversation.ActiveServantId != servant) return;
                portrait.SetExpression(semantic);
            }));
        };
        viewModel.OpenRequested += OnOpenRequested;
        viewModel.NavigationRequested += OnNavigationRequested;
        viewModel.Conversation.PropertyChanged += OnConversationChanged;
        viewModel.Conversation.Turns.CollectionChanged += (_, _) => RefreshConversation();
        MessageScroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange < 0 && e.ExtentHeightChange == 0) _following = false;
            if (MessageScroller.ScrollableHeight - MessageScroller.VerticalOffset < 2) _following = true;
            JumpButton.Visibility = _following ? Visibility.Collapsed : Visibility.Visible;
            if (e.ExtentHeightChange > 0 && _following) MessageScroller.ScrollToEnd();
        };
        Closing += (_, e) =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            e.Cancel = true;
            Hide();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                viewModel.NotifyActivated();
                if (_showingTasks) _workspace?.EnterView();
            }
            else { ClearCopyFeedback(); CloseDrawers(); viewModel.NotifyWindowHidden(); viewModel.StopSpeech(); Hidden?.Invoke(); }
        };
        viewModel.Conversation.SessionChanged += ClearCopyFeedback;
        Dispatcher.ShutdownStarted += OnCopyDispatcherShutdown;
        Closed += (_, _) =>
        {
            ClearCopyFeedback();
            viewModel.Conversation.SessionChanged -= ClearCopyFeedback;
            Dispatcher.ShutdownStarted -= OnCopyDispatcherShutdown;
        };
        Activated += (_, _) => viewModel.NotifyActivated();
        Deactivated += (_, _) => viewModel.NotifyDeactivated();
        PreviewKeyDown += OnKeyDown;
        RefreshConversation();
        UpdateResponsiveLayout();
    }

    private async void OnProjectClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Conversation.IsStreaming) return;
        var menu = new ContextMenu { PlacementTarget = ProjectButton };
        ProjectButton.ContextMenu = menu;
        var clear = new MenuItem { Header = "不关联项目", IsCheckable = true,
            IsChecked = string.IsNullOrEmpty(_viewModel.Conversation.SessionContext.ProjectId) };
        clear.Click += (_, _) => { if (!_viewModel.Conversation.IsStreaming) _viewModel.RemoveContextChip("project"); };
        menu.Items.Add(clear);
        var status = new MenuItem { Header = "正在读取项目…", IsEnabled = false };
        menu.Items.Add(status);
        menu.IsOpen = true;
        await _viewModel.ProjectSelection.RefreshAsync();
        if (!menu.IsOpen || !IsVisible) return;
        menu.Items.Remove(status);
        foreach (var option in _viewModel.ProjectSelection.Projects)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true,
                IsChecked = option.Id == _viewModel.Conversation.SessionContext.ProjectId };
            item.Click += (_, _) => { if (!_viewModel.Conversation.IsStreaming) _viewModel.SelectProject(option); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuItem { Header = _viewModel.ProjectSelection.StatusText, IsEnabled = false });
    }

    private void OnOpenRequested() { Show(); Activate(); if (!_showingTasks) InputBox.Focus(); }
    private void OnNavigationRequested(object? sender, MainNavigationTarget target)
    {
        if (target is MainNavigationTarget.Schedule or MainNavigationTarget.TodoDetail or MainNavigationTarget.AgentReconciliation) { ShowTasks(true); _workspace?.FocusTodo(_viewModel.CurrentContext.SelectedId); }
        else ShowTasks(false);
    }
    private void OnConversationChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshConversation();
        if (e.PropertyName == nameof(ConversationViewModel.IsStreaming))
        {
            if (_viewModel.Conversation.IsStreaming) _viewModel.ModelSelection.BeginGeneration();
            else _viewModel.ModelSelection.EndGeneration();
        }
    }
    private void RefreshConversation()
    {
        var error = _viewModel.Conversation.ErrorText;
        var stopped = error == "已取消。";
        FailureNotice.Visibility = !string.IsNullOrWhiteSpace(error) && !stopped ? Visibility.Visible : Visibility.Collapsed;
        StoppedNotice.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = _viewModel.Conversation.Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateResponsiveLayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var constrained = DialogueWindowViewModel.GetResponsiveLayoutState(width, height) == ResponsiveLayoutState.Constrained;
        WelcomeSecondaryCopy.Visibility = constrained ? Visibility.Collapsed : Visibility.Visible;
        WelcomeShortcuts.Visibility = constrained ? Visibility.Collapsed : Visibility.Visible;
        HeaderSubtitle.Visibility = constrained ? Visibility.Collapsed : Visibility.Visible;
        NavigationColumn.Width = new GridLength(constrained ? 56 : 64);
        HeaderRow.Height = new GridLength(constrained ? 76 : 90);
        RoleAvatar.Width = RoleAvatar.Height = constrained ? 44 : 54;
        IdentityHeader.Margin = constrained ? new Thickness(16, 10, 12, 10) : new Thickness(24, 12, 24, 12);
        ChatBody.Margin = constrained ? new Thickness(16, 12, 16, 14) : new Thickness(24, 16, 24, 18);
    }
    private void ShowTasks(bool tasks)
    {
        if (tasks) ClearCopyFeedback();
        var enteringTasks = tasks && !_showingTasks;
        CloseDrawers();
        _showingTasks = tasks;
        ChatBody.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        TasksPage.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
        NewConversationButton.Visibility = HistoryButton.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        ChatTabButton.SetResourceReference(StyleProperty, tasks ? "ShellRailButton" : "ShellRailSelectedButton");
        TasksButton.SetResourceReference(StyleProperty, tasks ? "ShellRailSelectedButton" : "ShellRailButton");
        Title = tasks ? "FGO Pet · 待办" : "FGO Pet · 聊天";
        if (tasks)
        {
            if (enteringTasks) _workspace?.EnterView();
            else _workspace?.Refresh();
        }
    }
    private void OpenDrawer(bool tasks)
    {
        if (tasks) { ShowTasks(true); return; }
        ShowTasks(false);
        _viewModel.Conversation.LoadHistory();
        _drawerOrigin = HistoryButton;
        HistoryDrawer.Visibility = Backdrop.Visibility = Visibility.Visible;
        ChatBody.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(ChatBody, KeyboardNavigationMode.None);
        HistoryDrawer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void CloseDrawers()
    {
        _viewModel.Conversation.CancelHistoryDeletion();
        _historyDeleteOrigin = null;
        HistoryDrawer.Visibility = Backdrop.Visibility = Visibility.Collapsed;
        ChatBody.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(ChatBody, KeyboardNavigationMode.Continue);
    }
    private void OnHistoryClick(object sender, RoutedEventArgs e) => OpenDrawer(false);
    private void OnTasksClick(object sender, RoutedEventArgs e) => ShowTasks(true);
    private void OnFocusShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_panel is null) return;
        if (_panel.State == FgoPet.Core.Panels.AttachedPanelState.Collapsed)
            _panel.PortraitClick();
        if (_panel.State != FgoPet.Core.Panels.AttachedPanelState.ExpandedFocus)
            _panel.FocusClick();
        Hide();
    }
    private void OnChatClick(object sender, RoutedEventArgs e) => ShowTasks(false);
    private void OnSettingsClick(object sender, RoutedEventArgs e) => _viewModel.NavigateToSettings(SettingsSection.Personalization);
    private void OnAvatarFailed(object sender, ExceptionRoutedEventArgs e) => RoleImage.SetCurrentValue(Image.SourceProperty, null);
    private void OnCloseClick(object sender, RoutedEventArgs e) { CloseDrawers(); _drawerOrigin?.Focus(); }
    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();
    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Conversation.NewConversationCommand.CanExecute(null))
            _viewModel.Conversation.NewConversationCommand.Execute(null);
        CloseDrawers();
        ShowTasks(false);
        _following = true;
        MessageScroller.ScrollToEnd();
        InputBox.Focus();
    }
    private void OnHistoryOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConversationHistoryItem item }) OpenHistoryConversation(item);
    }
    private void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem && HistoryList.SelectedItem is ConversationHistoryItem item)
        { OpenHistoryConversation(item); e.Handled = true; }
    }
    private void OpenHistoryConversation(ConversationHistoryItem item)
    {
        _viewModel.StopSpeech();
        _following = true;
        _viewModel.Conversation.SetActiveConversation(item.ConversationId);
        Dispatcher.BeginInvoke(new Action(() => MessageScroller.ScrollToEnd()));
        CloseDrawers();
    }
    private void OnHistoryDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConversationHistoryItem item } button) return;
        _viewModel.Conversation.RequestHistoryDeletion(item);
        if (!_viewModel.Conversation.HasPendingHistoryDeletion) return;
        _historyDeleteOrigin = button;
        UpdateLayout();
        CancelHistoryDeleteButton.Focus();
    }
    private void OnCancelHistoryDeleteClick(object sender, RoutedEventArgs e) => CancelHistoryDeletion();
    private void CancelHistoryDeletion()
    {
        _viewModel.Conversation.CancelHistoryDeletion();
        _historyDeleteOrigin?.Focus();
        _historyDeleteOrigin = null;
    }
    private void OnConfirmHistoryDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.Conversation.ConfirmHistoryDeletion()) return;
        _historyDeleteOrigin = null;
        HistoryList.Focus();
    }
    private void OnHistoryRefreshClick(object sender, RoutedEventArgs e)
    {
        CancelHistoryDeletion();
        _viewModel.Conversation.LoadHistory();
    }
    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        ShowTasks(false);
        _following = true;
        MessageScroller.ScrollToEnd();
        JumpButton.Visibility = Visibility.Collapsed;
    }
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!IsVisible || Dispatcher.HasShutdownStarted) return;
        if (sender is Button { Tag: ConversationTurnViewModel turn } && turn.Actions.CanCopy)
        {
            var button = (Button)sender;
            var feedbackIcon = FindResource("ChatCopied");
            var feedbackText = "已复制";
            try { _clipboard.SetText(turn.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                feedbackIcon = FindResource("ChatCopyFailed");
                feedbackText = "复制失败，请重试";
            }
            button.Content = feedbackIcon;
            // Keep ToolTip as plain content. Assigning a ToolTip instance to a
            // Button that already has a XAML tooltip can make WPF attach the
            // same logical ToolTip twice and terminate the dispatcher.
            button.ToolTip = feedbackText;
            System.Windows.Automation.AutomationProperties.SetName(button, feedbackText);
            if (_copyFeedback.Remove(button, out var previous)) previous.Stop();
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_copyFeedback.TryGetValue(button, out var current) || !ReferenceEquals(timer, current)) return;
                _copyFeedback.Remove(button);
                if (!Dispatcher.HasShutdownStarted) ResetCopyFeedback(button);
            };
            _copyFeedback[button] = timer;
            timer.Start();
        }
    }

    private void ResetCopyFeedback(Button button)
    {
        button.Content = FindResource("ChatCopy");
        button.ToolTip = "复制";
        System.Windows.Automation.AutomationProperties.SetName(button, "复制");
    }

    private void OnCopyDispatcherShutdown(object? sender, EventArgs e) => ClearCopyFeedback();

    private void ClearCopyFeedback()
    {
        foreach (var (button, timer) in _copyFeedback)
        {
            timer.Stop();
            if (!Dispatcher.HasShutdownStarted) ResetCopyFeedback(button);
        }
        _copyFeedback.Clear();
    }
    private async void OnSpeechClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConversationTurnViewModel turn } || !turn.CanReadAloud) return;
        if (turn.SpeechNeedsConfiguration) _viewModel.NavigateToSettings(SettingsSection.Speech);
        else await _viewModel.ReadAloudAsync(turn);
    }
    private void OnViewTodoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConversationTurnViewModel turn }
            || !turn.CanViewTodo
            || string.IsNullOrWhiteSpace(turn.CreatedTodoId)) return;

        ShowTasks(true);
        _workspace?.FocusTodo(turn.CreatedTodoId);
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Backdrop.Visibility == Visibility.Visible)
        {
            if (_viewModel.Conversation.HasPendingHistoryDeletion) CancelHistoryDeletion();
            else CloseDrawers();
            e.Handled = true; return;
        }
        if (e.Key != Key.Enter || !InputBox.IsKeyboardFocusWithin || _composing || e.IsRepeat ||
            Keyboard.Modifiers != ModifierKeys.None) return;
        var command = _viewModel.Composer.SendOrStopCommand;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }
}
