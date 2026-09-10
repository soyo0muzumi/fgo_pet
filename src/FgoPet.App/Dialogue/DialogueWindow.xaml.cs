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
    private bool _composing;
    private bool _following = true;
    private Button? _drawerOrigin;
    internal ContextMenu? LastMoreMenu { get; private set; }
    public event Action? Hidden;

    public DialogueWindow(DialogueWindowViewModel viewModel, TodoListViewModel? todos = null,
        FgoPet.App.Panels.AttachedPanelViewModel? panel = null, SettingsViewModel? settingsNavigation = null)
    {
        _viewModel = viewModel;
        _todos = todos;
        InitializeComponent();
        DataContext = viewModel;
        if (todos is not null) TaskList.ItemsSource = todos.VisibleItems;
        TextCompositionManager.AddPreviewTextInputStartHandler(InputBox, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(InputBox, (_, _) => _composing = false);
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
            if (IsVisible) viewModel.NotifyActivated();
            else { viewModel.NotifyWindowHidden(); viewModel.StopSpeech(); Hidden?.Invoke(); }
        };
        Activated += (_, _) => viewModel.NotifyActivated();
        Deactivated += (_, _) => viewModel.NotifyDeactivated();
        PreviewKeyDown += OnKeyDown;
        RefreshConversation();
    }

    private void OnOpenRequested() { Show(); Activate(); InputBox.Focus(); }
    private void OnNavigationRequested(object? sender, MainNavigationTarget target)
    {
        if (target == MainNavigationTarget.Schedule) OpenDrawer(true);
        else CloseDrawers();
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
    private void OpenDrawer(bool tasks)
    {
        CloseDrawers();
        _drawerOrigin = tasks ? TasksButton : HistoryButton;
        if (tasks) _todos?.Refresh();
        TasksDrawer.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
        HistoryDrawer.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        Backdrop.Visibility = Visibility.Visible;
        ChatBody.IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(ChatBody, KeyboardNavigationMode.None);
        var drawer = tasks ? TasksDrawer : HistoryDrawer;
        drawer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void CloseDrawers()
    {
        HistoryDrawer.Visibility = TasksDrawer.Visibility = Backdrop.Visibility = Visibility.Collapsed;
        ChatBody.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(ChatBody, KeyboardNavigationMode.Continue);
        _drawerOrigin?.Focus();
    }
    private void OnHistoryClick(object sender, RoutedEventArgs e) => OpenDrawer(false);
    private void OnTasksClick(object sender, RoutedEventArgs e) => OpenDrawer(true);
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseDrawers();
    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();
    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Conversation.NewConversationCommand.CanExecute(null))
            _viewModel.Conversation.NewConversationCommand.Execute(null);
        CloseDrawers();
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
            var feedback = new ToolTip { Content = feedbackText, PlacementTarget = button };
            button.Content = feedbackIcon;
            button.ToolTip = feedback;
            System.Windows.Automation.AutomationProperties.SetName(button, feedbackText);
            feedback.IsOpen = true;
            await Task.Delay(1200);
            feedback.IsOpen = false;
            // An older click must not reset newer feedback.
            if (!ReferenceEquals(button.ToolTip, feedback)) return;
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