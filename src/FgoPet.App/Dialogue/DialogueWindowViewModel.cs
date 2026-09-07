using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Portraits;

namespace FgoPet.App.Dialogue;

/// <summary>
/// Hosts the standalone dialogue window around the single <see cref="ConversationViewModel"/>
/// instance. Adds no conversation state of its own: opening the window reuses the
/// live session, closing only hides, and read receipts (unread reply count) are
/// governed here so the compact panel keeps a single source of truth.
/// </summary>
public sealed partial class DialogueWindowViewModel : ObservableObject
{
    private readonly ConversationViewModel _conversation;
    private bool _windowActive;

    public DialogueWindowViewModel(ConversationViewModel conversation)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        conversation.Turns.CollectionChanged += OnTurnsChanged;
    }

    /// <summary>Raised when the window should be presented, focused, or repositioned.</summary>
    public event Action? OpenRequested;

    public ConversationViewModel Conversation => _conversation;

    /// <summary>Assistant replies that arrived while the window was hidden or inactive.</summary>
    [ObservableProperty]
    private int _unreadCount;

    /// <summary>Raised on unread changes so the compact panel can refresh its badge and pill.</summary>
    public event Action? UnreadChanged;

    /// <summary>External request to present the window; also marks everything read.</summary>
    public void RequestOpen()
    {
        OpenRequested?.Invoke();
    }

    /// <summary>The window became visible and frontmost: clear the badge.</summary>
    public void NotifyActivated()
    {
        _windowActive = true;
        if (UnreadCount != 0)
        {
            UnreadCount = 0;
            UnreadChanged?.Invoke();
        }
    }

    /// <summary>The window lost frontmost status but may still be visible.</summary>
    public void NotifyDeactivated()
    {
        _windowActive = false;
    }

    /// <summary>The window is hidden again.</summary>
    public void NotifyWindowHidden()
    {
        _windowActive = false;
    }

    /// <summary>R6 (spec §8.2): expression-change hook for dialogue events. No
    /// caller in this version; future reply flows will switch the portrait here.</summary>
    public void RequestExpression(ExpressionSemantic semantic)
    {
        ExpressionChangeRequested?.Invoke(semantic);
    }

    /// <summary>Raised by <see cref="RequestExpression"/>; the portrait layer subscribes.</summary>
    public event Action<ExpressionSemantic>? ExpressionChangeRequested;

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A reset with content is a bulk reload; treat it as one unread reply. An
        // empty reset is the servant-switch/new-conversation clear, never a reply.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            if (_conversation.Turns.Count > 0)
            {
                AddUnread(1);
            }
            return;
        }

        if (_windowActive || e.NewItems is null)
        {
            return;
        }

        var assistantReplies = 0;
        foreach (var item in e.NewItems)
        {
            if (item is ConversationTurnViewModel { Role: ChatMessageRole.Assistant })
            {
                assistantReplies++;
            }
        }

        if (assistantReplies > 0)
        {
            AddUnread(assistantReplies);
        }
    }

    private void AddUnread(int count)
    {
        UnreadCount += count;
        UnreadChanged?.Invoke();
    }
}
