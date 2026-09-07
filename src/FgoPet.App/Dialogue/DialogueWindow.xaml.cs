using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Dialogue;

/// <summary>
/// Standalone non-modal dialogue window (spec §8.1): standard OS chrome, shared
/// <see cref="ConversationViewModel"/>, and close-to-hide so streaming survives.
/// Auto-scrolls the message stream while the user stays at the bottom; scrolling
/// up pauses follow and offers a jump back.
/// </summary>
public partial class DialogueWindow : Window
{
    private readonly DialogueWindowViewModel _viewModel;
    private bool _userScrolledUp;

    /// <summary>Raised whenever the window hides (user close); placement save hooks here.</summary>
    public event Action? Hidden;

    public DialogueWindow(DialogueWindowViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        viewModel.OpenRequested += OnOpenRequested;
        viewModel.Conversation.PropertyChanged += OnConversationPropertyChanged;
        viewModel.Conversation.Turns.CollectionChanged += (_, _) => OnTurnsChanged();
        MessageScroller.ScrollChanged += OnScrollChanged;
        Deactivated += (_, _) => _viewModel.NotifyDeactivated();
        Activated += (_, _) => _viewModel.NotifyActivated();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue!)
            {
                _viewModel.NotifyActivated();
            }
            else
            {
                _viewModel.NotifyWindowHidden();
                Hidden?.Invoke();
            }
        };
        Closing += OnClosing;
        UpdateStateVisibility();
    }

    private void OnOpenRequested()
    {
        Show();
        Activate();
        JumpToLatest();
    }

    /// <summary>System-hide path (minimize/deactivate); close goes through OnClosing.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            _viewModel.NotifyDeactivated();
        }
    }

    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConversationViewModel.IsConversationEmpty)
            or nameof(ConversationViewModel.IsConfigurationRequired)
            or nameof(ConversationViewModel.IsEmptyStateVisible)
            or nameof(ConversationViewModel.IsConfigurationStateVisible))
        {
            UpdateStateVisibility();
        }
    }

    private void UpdateStateVisibility()
    {
        var conversation = _viewModel.Conversation;
        var empty = conversation.Turns.Count == 0;
        EmptyState.Visibility = empty && !conversation.IsConfigurationRequired
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConfigurationCard.Visibility = conversation.IsConfigurationStateVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnTurnsChanged()
    {
        UpdateStateVisibility();
        if (!_userScrolledUp)
        {
            JumpToLatest();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0)
        {
            return;
        }

        var scroll = MessageScroller.ScrollableHeight;
        var atBottom = scroll <= 0.5 || MessageScroller.VerticalOffset >= scroll - 0.5;
        if (!atBottom && e.VerticalChange < 0)
        {
            _userScrolledUp = true;
        }
        else if (atBottom)
        {
            _userScrolledUp = false;
        }

        JumpToLatestButton.Visibility = _userScrolledUp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnJumpToLatestClick(object sender, RoutedEventArgs e) => JumpToLatest();

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is ConversationHistoryItem item)
        {
            _viewModel.Conversation.SetActiveConversation(item.ConversationId);
        }
    }

    private void JumpToLatest()
    {
        _userScrolledUp = false;
        MessageScroller.ScrollToEnd();
        JumpToLatestButton.Visibility = Visibility.Collapsed;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        Hidden?.Invoke();
    }
}
