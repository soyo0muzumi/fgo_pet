using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Speech;
using FgoPet.Core.Portraits;
using FgoPet.App.Settings;
using FgoPet.App.Speech;
using FgoPet.App.Servants;

namespace FgoPet.App.Dialogue;

public enum MainNavigationTarget
{
    Companion,
    Schedule,
    Servant,
    Preferences,
    RolePackageImport,
    TodoDetail,
    MemoryReview,
    AgentReconciliation,
}

public sealed record NavigationContext(
    MainNavigationTarget Target,
    string? Filter = null,
    string? SelectedId = null,
    double ScrollOffset = 0);

public enum ResponsiveLayoutState
{
    Wide,
    Narrow,
    Constrained,
}

/// <summary>
/// Hosts the standalone dialogue window around the single <see cref="ConversationViewModel"/>
/// instance. Adds no conversation state of its own: opening the window reuses the
/// live session, closing only hides, and read receipts (unread reply count) are
/// governed here so the compact panel keeps a single source of truth.
/// </summary>
public sealed partial class DialogueWindowViewModel : ObservableObject
{
    private readonly ConversationViewModel _conversation;
    private readonly ServantLibraryViewModel? _library;
    private readonly SpeechPlaybackCoordinator? _speech;
    private readonly IDialogueProjectCatalog? _projectCatalog;
    private ConversationTurnViewModel? _activeSpeechTurn;
    private long _speechRequestId;
    private bool _windowActive;
    private readonly Dictionary<MainNavigationTarget, NavigationContext> _contexts = [];
    private readonly Stack<NavigationContext> _history = [];

    public DialogueWindowViewModel(
        ConversationViewModel conversation,
        ServantLibraryViewModel? library = null,
        SpeechPlaybackCoordinator? speech = null,
        IDialogueProjectCatalog? projectCatalog = null)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _library = library;
        _speech = speech;
        _projectCatalog = projectCatalog;
        Composer = new DialogueComposerViewModel(conversation);
        ActionCards = new DialogueActionCardHostViewModel(conversation.TodoProposals, conversation.ArchiveDrafts);
        ToolDrawer = new DialogueToolDrawerViewModel();
        ProjectSelection = new DialogueProjectSelectionViewModel(_projectCatalog);
        ModelSelection = conversation.ModelAuthority is null
            ? new DialogueModelSelectionViewModel(new EmptyConfiguredModelAuthority(), conversation.ModelStatusText)
            : new DialogueModelSelectionViewModel(conversation.ModelAuthority, conversation.ModelStatusText);
        ModelSelection.ModelSelected += modelId => conversation.SelectModelForFutureRequests(modelId);
        ToolDrawer.ToolSelected += OnToolSelected;
        conversation.Turns.CollectionChanged += OnTurnsChanged;
        conversation.AssistantReplyCompleted += OnAssistantReplyCompleted;
        conversation.SessionChanged += OnSessionChanged;
        if (_speech is not null) _speech.StateChanged += OnSpeechStateChanged;
        conversation.PropertyChanged += OnConversationPropertyChanged;
        if (_library is not null)
        {
            _library.PropertyChanged += OnLibraryPropertyChanged;
        }
    }

    /// <summary>Raised when the window should be presented, focused, or repositioned.</summary>
    public event Action? OpenRequested;

    public event EventHandler<MainNavigationTarget>? NavigationRequested;

    public event Action<SettingsSection>? SettingsRequested;

    public ConversationViewModel Conversation => _conversation;

    public SpeechPlaybackCoordinator? Speech => _speech;

    public async Task<SpeechPlaybackResult?> ReadAloudAsync(ConversationTurnViewModel turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (_speech is null || !turn.CanReadAloud)
        {
            return null;
        }

        if (ReferenceEquals(_activeSpeechTurn, turn) && turn.IsSpeechBusy)
        {
            StopSpeech();
            return new SpeechPlaybackResult(false, SpeechPlaybackState.Stopped, "朗读已停止。");
        }

        var requestId = BeginSpeech(turn);
        try
        {
            var result = await _speech.PlayConfiguredAsync(turn.Text).ConfigureAwait(true);
            ApplySpeechResult(turn, requestId, result);
            return result;
        }
        catch (ObjectDisposedException)
        {
            ClearSpeechTurn(turn, requestId);
            return null;
        }
    }

    public void StopSpeech()
    {
        var active = _activeSpeechTurn;
        _activeSpeechTurn = null;
        _speechRequestId++;
        if (active is not null)
        {
            ResetSpeechPresentation(active);
        }

        _speech?.Stop();
    }

    private void OnSpeechStateChanged(object? sender, EventArgs e)
    {
        var turn = _activeSpeechTurn;
        if (turn is null || _speech is null)
        {
            return;
        }

        switch (_speech.State)
        {
            case SpeechPlaybackState.Preparing:
                turn.IsSpeechBusy = true;
                turn.SpeechActionText = "停止朗读";
                turn.SpeechStatusText = "准备朗读…";
                break;
            case SpeechPlaybackState.Playing:
                turn.IsSpeechBusy = true;
                turn.SpeechActionText = "停止朗读";
                turn.SpeechStatusText = "正在朗读…";
                break;
            case SpeechPlaybackState.Failed:
                turn.IsSpeechBusy = false;
                turn.SpeechActionText = "重试朗读";
                if (string.IsNullOrWhiteSpace(turn.SpeechStatusText))
                {
                    turn.SpeechStatusText = "朗读失败，可重试。";
                }
                break;
            case SpeechPlaybackState.Stopped:
                turn.IsSpeechBusy = false;
                turn.SpeechActionText = "重试朗读";
                turn.SpeechStatusText = "朗读已停止。";
                break;
            case SpeechPlaybackState.Ended:
                _activeSpeechTurn = null;
                ResetSpeechPresentation(turn);
                break;
        }
    }

    private long BeginSpeech(ConversationTurnViewModel turn)
    {
        if (_activeSpeechTurn is not null && !ReferenceEquals(_activeSpeechTurn, turn))
        {
            ResetSpeechPresentation(_activeSpeechTurn);
        }

        _activeSpeechTurn = turn;
        var requestId = ++_speechRequestId;
        turn.IsSpeechBusy = true;
        turn.SpeechActionText = "停止朗读";
        turn.SpeechStatusText = "准备朗读…";
        turn.SpeechNeedsConfiguration = false;
        return requestId;
    }

    private void ApplySpeechResult(
        ConversationTurnViewModel turn,
        long requestId,
        SpeechPlaybackResult result)
    {
        if (requestId != _speechRequestId || !ReferenceEquals(_activeSpeechTurn, turn))
        {
            return;
        }

        switch (result.State)
        {
            case SpeechPlaybackState.Ended:
                _activeSpeechTurn = null;
                ResetSpeechPresentation(turn);
                break;
            case SpeechPlaybackState.Stopped:
                turn.IsSpeechBusy = false;
                turn.SpeechActionText = "重试朗读";
                turn.SpeechStatusText = result.SafeError ?? "朗读已停止。";
                break;
            case SpeechPlaybackState.Failed:
                turn.IsSpeechBusy = false;
                turn.SpeechNeedsConfiguration = IsSpeechConfigurationError(result.SafeError);
                turn.SpeechActionText = turn.SpeechNeedsConfiguration ? "去朗读设置" : "重试朗读";
                turn.SpeechStatusText = result.SafeError ?? "朗读失败，可重试。";
                break;
            case SpeechPlaybackState.Idle:
                if (result.SafeError is "自动朗读未启用。" or "没有可朗读的正文。")
                {
                    _activeSpeechTurn = null;
                    ResetSpeechPresentation(turn);
                }
                else
                {
                    turn.IsSpeechBusy = false;
                    turn.SpeechActionText = "朗读";
                    turn.SpeechStatusText = result.SafeError ?? string.Empty;
                }
                break;
        }
    }

    private async Task AutoReadAsync(ConversationTurnViewModel turn)
    {
        if (_speech is null || !_windowActive || !turn.CanReadAloud)
        {
            return;
        }

        var requestId = BeginSpeech(turn);
        try
        {
            var result = await _speech.PlayConfiguredAsync(turn.Text, autoRead: true).ConfigureAwait(true);
            ApplySpeechResult(turn, requestId, result);
        }
        catch (ObjectDisposedException)
        {
            ClearSpeechTurn(turn, requestId);
        }
    }

    private void ClearSpeechTurn(ConversationTurnViewModel turn, long requestId)
    {
        if (requestId != _speechRequestId || !ReferenceEquals(_activeSpeechTurn, turn))
        {
            return;
        }

        _activeSpeechTurn = null;
        ResetSpeechPresentation(turn);
    }

    private static void ResetSpeechPresentation(ConversationTurnViewModel turn)
    {
        turn.IsSpeechBusy = false;
        turn.SpeechActionText = "朗读";
        turn.SpeechStatusText = string.Empty;
        turn.SpeechNeedsConfiguration = false;
    }

    private static bool IsSpeechConfigurationError(string? error) =>
        error?.Contains("朗读设置", StringComparison.Ordinal) == true
        || error?.Contains("语音服务地址无效", StringComparison.Ordinal) == true
        || error?.Contains("未知语音服务", StringComparison.Ordinal) == true;
    public DialogueComposerViewModel Composer { get; }

    public DialogueActionCardHostViewModel ActionCards { get; }

    public DialogueToolDrawerViewModel ToolDrawer { get; }

    public DialogueModelSelectionViewModel ModelSelection { get; }

    public DialogueProjectSelectionViewModel ProjectSelection { get; }

    public bool SelectProject(DialogueProjectOption option) =>
        ProjectSelection.TrySelect(option, _conversation.SessionContext);

    public bool RemoveContextChip(string chipId) =>
        _conversation.SessionContext.TryRemove(chipId);

    private void OnAssistantReplyCompleted(ConversationTurnViewModel turn)
    {
        if (_speech is null || !_windowActive || !turn.CanReadAloud)
        {
            return;
        }

        _ = AutoReadAsync(turn);
    }

    private void OnSessionChanged() => StopSpeech();

    private void OnToolSelected(string toolId) => _conversation.SessionContext.TrySetIntent(toolId);
    private sealed class EmptyConfiguredModelAuthority : IConfiguredModelAuthority
    {
        public IReadOnlyList<DialogueModelChoice> AvailableModels { get; } = Array.Empty<DialogueModelChoice>();
        public event EventHandler? Changed { add { } remove { } }
        public bool IsAvailable(string modelId) => false;
    }

    [ObservableProperty]
    private MainNavigationTarget _currentTarget = MainNavigationTarget.Companion;

    public NavigationContext CurrentContext =>
        _contexts.TryGetValue(CurrentTarget, out var context)
            ? context
            : new NavigationContext(CurrentTarget);

    public SettingsSection RequestedSettingsSection { get; private set; } = SettingsSection.Personalization;

    public static ResponsiveLayoutState GetResponsiveLayoutState(double clientWidth) =>
        GetResponsiveLayoutState(clientWidth, height: double.PositiveInfinity);

    public static ResponsiveLayoutState GetResponsiveLayoutState(double clientWidth, double height)
    {
        if (double.IsNaN(clientWidth) || double.IsInfinity(clientWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth));
        }

        if (double.IsNaN(height) || (!double.IsPositiveInfinity(height) && height <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (height < 520)
        {
            return ResponsiveLayoutState.Constrained;
        }

        return clientWidth >= 900
            ? ResponsiveLayoutState.Wide
            : clientWidth >= 720
                ? ResponsiveLayoutState.Narrow
                : ResponsiveLayoutState.Constrained;
    }

    public void NavigateToSettings(SettingsSection section)
    {
        RequestedSettingsSection = section;
        SettingsRequested?.Invoke(section);
    }

    public string ActiveServantDisplayName =>
        _library?.SelectedServant?.DisplayName
        ?? (string.IsNullOrWhiteSpace(_conversation.ActiveServantId) ? "尚未选择从者" : _conversation.ActiveServantId);

    public ServantCardViewModel? SelectedServant => _library?.SelectedServant;

    public void SaveContext(string? filter = null, string? selectedId = null, double scrollOffset = 0)
    {
        if (double.IsNaN(scrollOffset) || double.IsInfinity(scrollOffset) || scrollOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scrollOffset));
        }

        _contexts[CurrentTarget] = new NavigationContext(CurrentTarget, filter, selectedId, scrollOffset);
    }

    public void NavigateTo(MainNavigationTarget target, string? filter = null, string? selectedId = null)
    {
        if (target == MainNavigationTarget.TodoDetail)
        {
            target = MainNavigationTarget.Schedule;
        }

        var settingsSection = target switch
        {
            MainNavigationTarget.Servant or MainNavigationTarget.RolePackageImport => SettingsSection.RolePackages,
            MainNavigationTarget.MemoryReview => SettingsSection.ConversationMemory,
            MainNavigationTarget.AgentReconciliation => SettingsSection.AgentConnection,
            MainNavigationTarget.Preferences => RequestedSettingsSection,
            _ => (SettingsSection?)null,
        };
        if (settingsSection is not null)
        {
            NavigateToSettings(settingsSection.Value);
            return;
        }

        var current = CurrentContext;
        SaveContext(current.Filter, current.SelectedId, current.ScrollOffset);
        _history.Push(CurrentContext);
        CurrentTarget = target;
        NavigationRequested?.Invoke(this, target);
        if (filter is not null || selectedId is not null)
        {
            _contexts[target] = new NavigationContext(target, filter, selectedId);
        }
    }

    public bool NavigateBack()
    {
        if (_history.Count == 0) return false;
        var context = _history.Pop();
        CurrentTarget = context.Target;
        NavigationRequested?.Invoke(this, context.Target);
        _contexts[context.Target] = context;
        return true;
    }

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
        StopSpeech();
    }
    /// <summary>The window is hidden again.</summary>
    public void NotifyWindowHidden()
    {
        _windowActive = false;
        StopSpeech();
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

    private void OnConversationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.ActiveServantId))
        {
            StopSpeech();
            OnPropertyChanged(nameof(ActiveServantDisplayName));
        }
    }

    private void OnLibraryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServantLibraryViewModel.SelectedServant))
        {
            OnPropertyChanged(nameof(ActiveServantDisplayName));
            OnPropertyChanged(nameof(SelectedServant));
        }
    }

    private void AddUnread(int count)
    {
        UnreadCount += count;
        UnreadChanged?.Invoke();
    }
}
