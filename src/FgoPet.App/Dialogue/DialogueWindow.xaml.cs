using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FgoPet.App.Settings;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Dialogue;

/// <summary>Chat presentation shell. Conversation and task state remain owned by their view models.</summary>
public partial class DialogueWindow : Window
{
    private readonly DialogueWindowViewModel _viewModel;
    private readonly TodoListViewModel? _todos;
    private readonly FgoPet.App.Panels.AttachedPanelViewModel? _panel;
    private FgoPet.App.Views.TodoWorkspaceView? _workspace;
    private bool _showingTasks;
    private bool _composing;
    private bool _following = true;
    private Button? _drawerOrigin;
    internal ContextMenu? LastMoreMenu { get; private set; }
    public event Action? Hidden;

    public DialogueWindow(DialogueWindowViewModel viewModel, TodoListViewModel? todos = null,
        FgoPet.App.Panels.AttachedPanelViewModel? panel = null, SettingsViewModel? settingsNavigation = null,
        FgoPet.App.Services.TodoApplicationService? todoService = null,
        FgoPet.Core.Agents.IAgentRepository? agents = null,
        AgentCurrentTaskViewModel? currentTask = null,
        FgoPet.App.Portraits.PortraitController? portrait = null)
    {
        _viewModel = viewModel;
        _todos = todos;
        _panel = panel;
        InitializeComponent();
        DataContext = viewModel;
        FocusShortcutButton.IsEnabled = panel is not null;
        FocusShortcutButton.ToolTip = panel is null ? "专注入口当前不可用" : "打开现有专注设置";
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
            else { viewModel.NotifyWindowHidden(); viewModel.StopSpeech(); Hidden?.Invoke(); }
        };
        Activated += (_, _) => viewModel.NotifyActivated();
        Deactivated += (_, _) => viewModel.NotifyDeactivated();
        PreviewKeyDown += OnKeyDown;
        RefreshConversation();
        UpdateResponsiveLayout();
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
        ChatBody.Margin = constrained ? new Thickness(14, 12, 14, 12) : new Thickness(22, 16, 22, 14);
    }
    private void ShowTasks(bool tasks)
    {
        var enteringTasks = tasks && !_showingTasks;
        CloseDrawers();
        _showingTasks = tasks;
        ChatBody.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        TasksPage.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
        NewConversationButton.Visibility = HistoryButton.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        ChatTabButton.FontWeight = tasks ? FontWeights.Normal : FontWeights.Bold;
        TasksButton.FontWeight = tasks ? FontWeights.Bold : FontWeights.Normal;
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
        _drawerOrigin = HistoryButton;
        HistoryDrawer.Visibility = Backdrop.Visibility = Visibility.Visible;
        ChatBody.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(ChatBody, KeyboardNavigationMode.None);
        HistoryDrawer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void CloseDrawers()
    {
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
    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not ConversationHistoryItem item) return;
        _viewModel.StopSpeech();
        _following = true;
        _viewModel.Conversation.SetActiveConversation(item.ConversationId);
        Dispatcher.BeginInvoke(new Action(() => MessageScroller.ScrollToEnd()));
        CloseDrawers();
    }
    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        ShowTasks(false);
        _following = true;
        MessageScroller.ScrollToEnd();
        JumpButton.Visibility = Visibility.Collapsed;
    }
    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConversationTurnViewModel turn } && turn.Actions.CanCopy)
        {
            var button = (Button)sender;
            var feedbackIcon = FindResource("ChatCopied");
            var feedbackText = "已复制";
            try { Clipboard.SetText(turn.Text); }
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
            await Task.Delay(1200);
            // An older click must not reset newer feedback.
            if (!Equals(button.ToolTip, feedbackText)) return;
            button.Content = FindResource("ChatCopy");
            button.ToolTip = "复制";
            System.Windows.Automation.AutomationProperties.SetName(button, "复制");
        }
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
        { CloseDrawers(); e.Handled = true; return; }
        if (e.Key != Key.Enter || !InputBox.IsKeyboardFocusWithin || _composing || e.IsRepeat ||
            Keyboard.Modifiers != ModifierKeys.None) return;
        var command = _viewModel.Composer.SendOrStopCommand;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }
}
