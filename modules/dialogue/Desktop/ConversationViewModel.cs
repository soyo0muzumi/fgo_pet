using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Settings;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.App.ViewModels;
using FgoPet.App.Archives;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

public sealed record ConversationHistoryItem(
    string ConversationId,
    string Title,
    DateTimeOffset UpdatedAtUtc,
    string Status)
{
    public string UpdatedText => UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public enum TodoNoticeKind
{
    None,
    Error,
    Empty,
    Fallback,
}

public sealed partial class ConversationViewModel : ObservableObject, IDisposable
{
    // Legacy tool-only turns have no user-facing text. This exact assistant/completed
    // sentinel is the only history entry hidden here; ordinary user text is untouched.
    private const string ToolProposalHistoryPlaceholder = "[工具调用：待办提案]";
    private readonly ConversationOrchestrator _orchestrator;
    private readonly IDialogueSettingsStore _settings;
    private readonly ModelConnectionViewModel? _modelConnection;
    private readonly TodoProposalService? _todoProposals;
    private readonly ArchiveDraftService? _archiveDrafts;
    private readonly IConfiguredModelAuthority? _modelAuthority;
    private readonly IConversationHistoryQuery _history;
    private ConversationHistoryCursor? _historyCursor;
    [ObservableProperty]
    private bool _filterHistoryByProject = true;
    partial void OnFilterHistoryByProjectChanged(bool value) => LoadHistory();
    private bool _acceptRequestUpdates;
    private string _activeConversationId = string.Empty;
    public string CurrentConversationId => _activeConversationId;
    private bool _configurationRequired;
    private long _sessionGeneration;
    private bool _disposed;
    private bool _restoringContext;
    private string _contextProjectId = string.Empty;
    private readonly StringBuilder _pendingReasoning = new();
    private readonly HashSet<string> _completedNotifications = new(StringComparer.Ordinal);
    public event Action<FgoPet.Core.Portraits.ExpressionSemantic>? ExpressionRequested;
    private long _thinkingStartedTicks;
    private System.Timers.Timer? _thinkingTimer;

    public ConversationViewModel(
        ConversationOrchestrator orchestrator,
        IDialogueSettingsStore settings,
        ModelConnectionViewModel? modelConnection = null,
        TodoProposalService? todoProposals = null,
        ArchiveDraftService? archiveDrafts = null,
        IConversationHistoryQuery? history = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _modelConnection = modelConnection;
        _todoProposals = todoProposals;
        _archiveDrafts = archiveDrafts;
        _history = history ?? orchestrator.HistoryQuery;
        _modelAuthority = modelConnection is null ? null : new DialogueModelConnectionSource(modelConnection);
        if (_modelConnection is not null)
        {
            _modelConnection.ConnectionSaved += OnConnectionSaved;
            _modelConnection.ShowReasoningChanged += OnShowReasoningChanged;
        }
        _orchestrator.Updated += OnConversationUpdated;
        SessionContext.PropertyChanged += OnSessionContextChanged;
        var model = _settings.Load().ModelConnection;
        ProviderStatusText = model?.ProviderId ?? "未配置供应商";
        ModelStatusText = model?.ModelId ?? "未配置模型";
        _configurationRequired = model is null;
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        SendOrStopCommand = new AsyncRelayCommand(SendOrStopAsync, () => CanSendOrStop);
        StopCommand = new RelayCommand(Stop, () => IsStreaming);
        NewConversationCommand = new RelayCommand(NewConversation);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        RetryTodoCommand = new AsyncRelayCommand(RetryTodoAsync, () => !IsStreaming && !string.IsNullOrWhiteSpace(_lastUserMessage) && !string.IsNullOrWhiteSpace(ActiveServantId));
        ManualTodoCommand = new RelayCommand(() => ManualTodoRequested?.Invoke());
        RetryHistoryCommand = new RelayCommand(LoadHistory);
        LoadMoreHistoryCommand = new RelayCommand(() => LoadHistoryPage(append: true), () => HasMoreHistory && !IsHistoryLoading && !_disposed);
    }

    /// <summary>Raised when the user asks to configure the model; the host owns the settings route.</summary>
    public event Action<SettingsSection>? SettingsRequested;

    public ObservableCollection<ConversationTurnViewModel> Turns { get; } = new();
    public ObservableCollection<TodoProposalViewModel> TodoProposals { get; } = new();
    public ObservableCollection<ArchiveDraftViewModel> ArchiveDrafts { get; } = new();
    public ObservableCollection<ConversationHistoryItem> History { get; } = new();
    public ObservableCollection<HistorySourceViewModel> RecalledSources { get; } = new();
    public DialogueSessionContextViewModel SessionContext { get; } = new();

    [ObservableProperty]
    private string _historyStatus = string.Empty;

    [ObservableProperty]
    private bool _isHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingHistoryDeletion))]
    private ConversationHistoryItem? _pendingHistoryDeletion;
    private string? _historyDeletionServantId;
    public bool HasPendingHistoryDeletion => PendingHistoryDeletion is not null;
    public bool CanDeleteHistory => !IsStreaming && !string.IsNullOrWhiteSpace(ActiveServantId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanSendOrStop))]
    [NotifyPropertyChangedFor(nameof(CanDeleteHistory))]
    private string _activeServantId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanSendOrStop))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanSendOrStop))]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    [NotifyPropertyChangedFor(nameof(CanDeleteHistory))]
    private bool _isStreaming;

    [ObservableProperty]
    private string _providerStatusText;

    [ObservableProperty]
    private string _modelStatusText;

    [ObservableProperty]
    private string _errorText = string.Empty;

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(CanOpenModelSettings));

    [ObservableProperty]
    private string _requestStatusText = string.Empty;

    [ObservableProperty]
    private string? _pendingTodoDraftId;

    [ObservableProperty]
    private int? _pendingTodoDraftVersion;

    [ObservableProperty]
    private int? _lastHttpStatusCode;

    /// <summary>Live "思考 · N.Ns" timer label; empty when not thinking (spec §9.3).</summary>
    [ObservableProperty]
    private string _thinkingTimerText = string.Empty;

    /// <summary>True while a reasoning stream is running but no content has arrived yet.</summary>
    [ObservableProperty]
    private bool _isThinking;

    /// <summary>Settings toggle (default on): window shows the reasoning well; compact never does.</summary>
    public bool ShowReasoning => _settings.Load().ShowReasoning;

    public bool CanOpenModelSettings =>
        ErrorText.Contains("模型连接", StringComparison.Ordinal)
        || ErrorText.Contains("模型服务地址", StringComparison.Ordinal)
        || ErrorText.Contains("模型 API Key", StringComparison.Ordinal)
        || ErrorText.Contains("无法连接模型服务", StringComparison.Ordinal)
        || ErrorText.Contains("对话服务暂时不可用", StringComparison.Ordinal);
    public bool CanSend => !_disposed && !IsStreaming
        && !string.IsNullOrWhiteSpace(ActiveServantId)
        && !string.IsNullOrWhiteSpace(InputText);

    public bool CanStop => IsStreaming;

    public bool CanSendOrStop => CanSend || CanStop;

    public string ActionLabel => IsStreaming ? "停止生成" : "发送消息";

    public bool SelectModelForFutureRequests(string modelId)
    {
        if (IsStreaming || string.IsNullOrWhiteSpace(modelId)) return false;
        var current = _settings.Load().ModelConnection;
        if (current is null) return false;
        var normalized = modelId.Trim();
        if (_modelAuthority is null || !_modelAuthority.IsAvailable(normalized)) return false;
        if (string.Equals(current.ModelId, normalized, StringComparison.Ordinal)) return true;
        _settings.Save(_settings.Load() with
        {
            ModelConnection = new ModelConnectionSettings(
                current.ProviderId, current.BaseUrl, normalized, current.ToolsSupported),
        });
        ModelStatusText = normalized;
        return true;
    }

    public IConfiguredModelAuthority? ModelAuthority => _modelAuthority;

    public sealed record TodoNoticeState(string Text, TodoNoticeKind Kind);

    private TodoNoticeState _todoNotice = new(string.Empty, TodoNoticeKind.None);
    private string? _lastUserMessage;

    /// <summary>Raised when the user asks to create a Todo manually; the host owns the route.</summary>
    public event Action? ManualTodoRequested;

    /// <summary>Raised only for a newly completed assistant reply in the live session.</summary>
    public event Action<ConversationTurnViewModel>? AssistantReplyCompleted;

    /// <summary>Raised when the active conversation identity is about to change.</summary>
    public event Action? SessionChanged;

    public TodoNoticeState TodoNotice => _todoNotice;

    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand SendOrStopCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand NewConversationCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand RetryTodoCommand { get; }
    public IRelayCommand ManualTodoCommand { get; }
    public IRelayCommand RetryHistoryCommand { get; }
    public IRelayCommand LoadMoreHistoryCommand { get; }
    public bool HasMoreHistory => _historyCursor is not null;

    private void SetTodoNotice(string text, TodoNoticeKind kind)
    {
        _todoNotice = new TodoNoticeState(text, kind);
        OnPropertyChanged(nameof(TodoNotice));
    }

    // Presentation-only state: the empty, configured, and configuration-required views.
    public bool IsConversationEmpty => Turns.Count == 0;

    public bool IsEmptyStateVisible => IsConversationEmpty && !_configurationRequired;

    public bool IsConfigurationRequired => _configurationRequired;

    public bool IsConfigurationStateVisible => _configurationRequired && IsConversationEmpty;

    public void NotifyConfigurationRequired()
    {
        _configurationRequired = true;
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }

    private void OpenSettings() => SettingsRequested?.Invoke(SettingsSection.ModelConnection);

    public void SetActiveServant(string servantId)
    {
        if (_disposed) return;
        var normalizedServantId = servantId?.Trim() ?? string.Empty;
        if (string.Equals(ActiveServantId, normalizedServantId, StringComparison.Ordinal))
        {
            return;
        }

        InvalidateSession();
        SessionChanged?.Invoke();
        Turns.Clear();
        StopThinkingTimer();
        _pendingReasoning.Clear();
        ClearTodoProposals();
        ArchiveDrafts.Clear();
        RestoreProjectContext(null);
        RecalledSources.Clear();
        _activeConversationId = string.Empty;
        ErrorText = string.Empty;
        ActiveServantId = normalizedServantId;
        _configurationRequired = false;
        RefreshModelStatus();
        OnPropertyChanged(nameof(IsConversationEmpty));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
        OnPropertyChanged(nameof(CanSend));
        LoadHistory();
    }

    public void LoadHistory() => LoadHistoryPage(append: false);

    private void OnSessionContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_disposed || _restoringContext || args.PropertyName != nameof(SessionContext.ProjectId) ||
            _contextProjectId == SessionContext.ProjectId) return;
        _contextProjectId = SessionContext.ProjectId;
        if (!string.IsNullOrWhiteSpace(ActiveServantId))
        {
            _orchestrator.StartNewConversation(ActiveServantId);
            ResetConversationView(clearContext: false);
            LoadHistory();
        }
    }

    private void RestoreProjectContext(Conversation? conversation)
    {
        _restoringContext = true;
        try
        {
            SessionContext.Clear();
            if (conversation?.ProjectId is { } project)
                SessionContext.TrySetProject(project, conversation.ProjectLabel ?? "已保存项目");
            _contextProjectId = SessionContext.ProjectId;
        }
        finally { _restoringContext = false; }
    }

    private void OpenSource(HistoryHit source)
    {
        var scope = new ConversationScope(ActiveServantId, SessionContext.ProjectId);
        if (!_orchestrator.CanOpenSource(scope, source))
        {
            ErrorText = "这条历史来源已不可用，请刷新后重试。";
            return;
        }
        SetActiveConversation(source.Anchor.ConversationId);
    }

    private void LoadHistoryPage(bool append)
    {
        if (_disposed) return;
        IsHistoryLoading = true;
        if (!append) { History.Clear(); _historyCursor = null; }
        if (string.IsNullOrWhiteSpace(ActiveServantId))
        {
            HistoryStatus = "暂无历史对话";
            IsHistoryLoading = false;
            OnPropertyChanged(nameof(HasMoreHistory));
            LoadMoreHistoryCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            var page = FilterHistoryByProject
                ? _history.ReadPage(new ConversationScope(ActiveServantId, SessionContext.ProjectId), before: append ? _historyCursor : null)
                : _history.ReadPage(ActiveServantId, before: append ? _historyCursor : null);
            foreach (var conversation in page.Items)
            {
                History.Add(new ConversationHistoryItem(
                    conversation.ConversationId,
                    conversation.Title,
                    conversation.UpdatedAtUtc,
                    conversation.IsArchived ? "已归档" : "正常"));
            }
            _historyCursor = page.Next;
            HistoryStatus = History.Count == 0 ? "暂无历史对话" : string.Empty;
        }
        catch (Exception)
        {
            HistoryStatus = "历史会话加载失败，请重试。";
        }
        finally
        {
            IsHistoryLoading = false;
            OnPropertyChanged(nameof(HasMoreHistory));
            LoadMoreHistoryCommand.NotifyCanExecuteChanged();
        }
    }

    public void SetActiveConversation(string conversationId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(ActiveServantId)) return;
        CancelHistoryDeletion();
        try
        {
            InvalidateSession();
            SessionChanged?.Invoke();
            var messages = _orchestrator.LoadConversation(conversationId, ActiveServantId);
            Turns.Clear();
            ClearTodoProposals();
            ArchiveDrafts.Clear();
            RestoreProjectContext(_orchestrator.GetConversation(conversationId, ActiveServantId));
            _activeConversationId = conversationId;
            foreach (var message in messages.Where(m => m.Role is ChatMessageRole.User or ChatMessageRole.Assistant))
            {
                if (message.Role == ChatMessageRole.Assistant
                    && message.Status == ChatMessageStatus.Completed
                    && string.Equals(message.Text, ToolProposalHistoryPlaceholder, StringComparison.Ordinal))
                {
                    continue;
                }

                Turns.Add(new ConversationTurnViewModel(message.MessageId, message.Role, message.Text));
            }
            ErrorText = string.Empty;
            OnPropertyChanged(nameof(IsConversationEmpty));
            OnPropertyChanged(nameof(IsEmptyStateVisible));
            OnPropertyChanged(nameof(IsConfigurationStateVisible));
        }
        catch (Exception)
        {
            ErrorText = "历史对话加载失败，请重试。";
        }
    }

    private void RefreshModelStatus()
    {
        var model = _settings.Load().ModelConnection;
        ProviderStatusText = model?.ProviderId ?? "未配置供应商";
        ModelStatusText = model?.ModelId ?? "未配置模型";
    }

    private void OnConnectionSaved(ModelConnectionSettings connection)
    {
        InvalidateSession();
        ProviderStatusText = connection.ProviderId;
        ModelStatusText = connection.ModelId;
        _configurationRequired = false;
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }


    private async Task SendAsync()
    {
        var draft = InputText;
        var text = draft.Trim();
        if (!CanSend || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var generation = _sessionGeneration;
        var completed = await SendCoreAsync(text);
        if (!_disposed && generation == _sessionGeneration && completed && string.Equals(InputText, draft, StringComparison.Ordinal))
        {
            InputText = string.Empty;
        }
    }

    private async Task<bool> SendCoreAsync(string text)
    {
        if (_disposed) return false;
        var generation = _sessionGeneration;
        var servant = ActiveServantId;
        ErrorText = string.Empty;
        SetTodoNotice(string.Empty, TodoNoticeKind.None);
        StopThinkingTimer();
        _pendingReasoning.Clear();
        IsStreaming = true;
        _acceptRequestUpdates = true;
        try
        {
            _lastUserMessage = text;
            var result = await _orchestrator.SendAsync(
                servant,
                text,
                CancellationToken.None,
                SessionContext.ToRequestContext());
            if (!IsCurrent()) return false;
            if (result.Status is ConversationSendStatus.ConfigurationRequired or ConversationSendStatus.Failed)
            {
                ErrorText = result.SafeError ?? "对话暂时不可用。";
                if (result.Status == ConversationSendStatus.ConfigurationRequired)
                {
                    NotifyConfigurationRequired();
                }
            }
            else
            {
                SessionContext.ClearTransient();
                _configurationRequired = false;
                OnPropertyChanged(nameof(IsConfigurationRequired));
                OnPropertyChanged(nameof(IsConfigurationStateVisible));
                OnPropertyChanged(nameof(IsEmptyStateVisible));
            }

            return result.Status == ConversationSendStatus.Completed;
        }
        finally
        {
            if (IsCurrent()) { IsStreaming = false; _acceptRequestUpdates = false; }
        }
        bool IsCurrent() => !_disposed && generation == _sessionGeneration && servant == ActiveServantId;
    }

    private void ApplyTodoOutcome(TodoToolCallOutcome outcome, string? detail, string? structuredResponse)
    {
        switch (outcome)
        {
            case TodoToolCallOutcome.ProposalsReady:
                if (TodoProposals.Count > 0)
                {
                    SetTodoNotice(string.Empty, TodoNoticeKind.None);
                }
                break;
            case TodoToolCallOutcome.InvalidToolCall:
                SetTodoNotice($"待办提案生成失败：{detail ?? "工具调用无法解析。"}", TodoNoticeKind.Error);
                break;
            case TodoToolCallOutcome.NoProposal:
                SetTodoNotice("本次未生成待办提案。可重试生成，或手动创建。", TodoNoticeKind.Empty);
                break;
            case TodoToolCallOutcome.TextFallback:
                SetTodoNotice("当前模型不支持工具箱，已使用文本提案兜底。", TodoNoticeKind.Fallback);
                break;
            case TodoToolCallOutcome.Confirmed:
                PendingTodoDraftId = null;
                PendingTodoDraftVersion = null;
                SetTodoNotice(string.Empty, TodoNoticeKind.None);
                break;
            case TodoToolCallOutcome.Cancelled:
                PendingTodoDraftId = null;
                PendingTodoDraftVersion = null;
                SetTodoNotice(string.Empty, TodoNoticeKind.None);
                break;
            case TodoToolCallOutcome.CommitUnknown:
                SetTodoNotice("待办写入结果暂时无法确认，草稿已保留。", TodoNoticeKind.Error);
                break;
        }
    }

    private async Task RetryTodoAsync()
    {
        if (IsStreaming || string.IsNullOrWhiteSpace(_lastUserMessage) || !CanSendBase)
        {
            return;
        }

        SetTodoNotice(string.Empty, TodoNoticeKind.None);
        await SendCoreAsync(_lastUserMessage);
    }

    private bool CanSendBase => !IsStreaming
        && !string.IsNullOrWhiteSpace(ActiveServantId)
        && !string.IsNullOrWhiteSpace(_lastUserMessage);

    private void Stop() => _orchestrator.CancelCurrent();

    private async Task SendOrStopAsync()
    {
        if (IsStreaming)
        {
            Stop();
            return;
        }

        await SendAsync();
    }

    public void RequestHistoryDeletion(ConversationHistoryItem item)
    {
        if (!CanDeleteHistory || !History.Contains(item)) return;
        _historyDeletionServantId = ActiveServantId;
        PendingHistoryDeletion = item;
        HistoryStatus = string.Empty;
    }

    public void CancelHistoryDeletion()
    {
        PendingHistoryDeletion = null;
        _historyDeletionServantId = null;
    }

    public bool ConfirmHistoryDeletion()
    {
        if (PendingHistoryDeletion is not { } item || !CanDeleteHistory
            || !string.Equals(_historyDeletionServantId, ActiveServantId, StringComparison.Ordinal)) return false;
        try
        {
            if (!_orchestrator.TryDeleteConversation(item.ConversationId, ActiveServantId))
            {
                HistoryStatus = "暂时无法删除，请等待回复结束后刷新重试。";
                return false;
            }
        }
        catch (Exception)
        {
            HistoryStatus = "对话删除失败，请重试。";
            return false;
        }
        CancelHistoryDeletion();
        if (_activeConversationId == item.ConversationId) ResetConversationView();
        LoadHistory();
        HistoryStatus = "对话已删除。" + HistoryStatus;
        return true;
    }

    private void NewConversation()
    {
        if (string.IsNullOrWhiteSpace(ActiveServantId))
        {
            return;
        }

        _orchestrator.StartNewConversation(ActiveServantId);
        ResetConversationView();
    }

    private void ResetConversationView(bool clearContext = true)
    {
        InvalidateSession();
        CancelHistoryDeletion();
        SessionChanged?.Invoke();
        Turns.Clear();
        StopThinkingTimer();
        _pendingReasoning.Clear();
        ClearTodoProposals();
        ArchiveDrafts.Clear();
        if (clearContext) RestoreProjectContext(null);
        RecalledSources.Clear();
        _activeConversationId = string.Empty;
        _lastUserMessage = null;
        PendingTodoDraftId = null;
        PendingTodoDraftVersion = null;
        RequestStatusText = string.Empty;
        LastHttpStatusCode = null;
        SetTodoNotice(string.Empty, TodoNoticeKind.None);
        RetryTodoCommand.NotifyCanExecuteChanged();
        ErrorText = string.Empty;
        _configurationRequired = false;
        OnPropertyChanged(nameof(IsConversationEmpty));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
    }

    private void OnConversationUpdated(ConversationUpdate update)
    {
        if (_disposed || !_acceptRequestUpdates) return;
        if (!string.IsNullOrWhiteSpace(update.ServantId)
            && !string.Equals(update.ServantId, ActiveServantId, StringComparison.Ordinal))
        {
            return;
        }

        if (update.Type == ConversationUpdateType.UserMessagePersisted)
        {
            if (!string.IsNullOrEmpty(_activeConversationId) && _activeConversationId != update.ConversationId)
            {
                return;
            }

            _activeConversationId = update.ConversationId;
            RequestStatusText = "正在准备请求…";
            ErrorText = string.Empty;
            Turns.Add(new ConversationTurnViewModel(
                update.MessageId ?? "user",
                ChatMessageRole.User,
                update.TextDelta ?? string.Empty));
            TrimTurns();
            LoadHistory();
            return;
        }

        if (string.IsNullOrEmpty(_activeConversationId) || update.ConversationId != _activeConversationId)
        {
            return;
        }

        switch (update.Type)
        {
            case ConversationUpdateType.HistorySources:
                RecalledSources.Clear();
                foreach (var source in update.HistorySources ?? [])
                    RecalledSources.Add(new HistorySourceViewModel(source, () => OpenSource(source)));
                break;
            case ConversationUpdateType.RequestStage:
                LastHttpStatusCode = update.HttpStatusCode;
                RequestStatusText = update.RequestStage switch
                {
                    ConversationRequestStage.Preparing => "正在准备请求…",
                    ConversationRequestStage.Compacting => "正在整理较早的对话…",
                    ConversationRequestStage.RequestStarted => "请求已发出 · 等待模型响应…",
                    ConversationRequestStage.ResponseHeadersReceived => "已收到模型响应",
                    ConversationRequestStage.StreamingReasoning => "正在思考…",
                    ConversationRequestStage.StreamingTool => "正在调用工具…",
                    ConversationRequestStage.StreamingAnswer => "正在生成回答…",
                    ConversationRequestStage.Completed => "",
                    ConversationRequestStage.Cancelled => "已取消",
                    ConversationRequestStage.Failed when update.HttpStatusCode is { } httpCode => $"请求失败 · HTTP {httpCode}",
                    ConversationRequestStage.Failed => "请求失败",
                    _ => RequestStatusText,
                };
                var activeAssistant = Turns.LastOrDefault(item => item.IsStreaming && item.IsAssistant);
                if (activeAssistant is not null && update.RequestStage is ConversationRequestStage.StreamingReasoning or ConversationRequestStage.RequestStarted)
                {
                    activeAssistant.SetReasoningSummary(RequestStatusText);
                }
                break;
            case ConversationUpdateType.AssistantDelta:
                var turn = Turns.FirstOrDefault(item => item.MessageId == update.MessageId);
                if (turn is null)
                {
                    // Reasoning deltas arrive before the message id exists; they
                    // attach to the streaming turn created below or the latest one.
                    if (!string.IsNullOrEmpty(update.ReasoningDelta))
                    {
                        var reasoningTurn = Turns.LastOrDefault(item => item.IsStreaming && item.Role == ChatMessageRole.Assistant);
                        reasoningTurn?.AppendReasoning(update.ReasoningDelta);
                        if (reasoningTurn is null)
                        {
                            // First reasoning delta with no message yet: hold the
                            // text until the assistant turn appears.
                            _pendingReasoning.Append(update.ReasoningDelta);
                            StartThinkingTimer();
                        }
                        break;
                    }

                    turn = new ConversationTurnViewModel(update.MessageId ?? "assistant", ChatMessageRole.Assistant, string.Empty, true);
                    if (_pendingReasoning.Length > 0)
                    {
                        turn.AppendReasoning(_pendingReasoning.ToString());
                        turn.SetReasoningSummary(string.IsNullOrWhiteSpace(RequestStatusText) ? "正在思考…" : RequestStatusText);
                        _pendingReasoning.Clear();
                        turn.IsThinkingActive = true;
                        StopThinkingTimer();
                    }
                    Turns.Add(turn);
                }

                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    turn.AppendReasoning(update.ReasoningDelta);
                    turn.SetReasoningSummary(string.IsNullOrWhiteSpace(RequestStatusText) ? "正在思考…" : RequestStatusText);
                    if (!turn.IsThinkingActive && turn.Text.Length == 0)
                    {
                        turn.IsThinkingActive = true;
                        StartThinkingTimer();
                    }
                    break;
                }

                if (turn.Text.Length == 0 && turn.ReasoningText.Length > 0)
                {
                    turn.ReasoningDurationText = ThinkingTimerText.Length > 0
                        ? ThinkingTimerText
                        : "思考";
                    StopThinkingTimer();
                }

                turn.Append(update.TextDelta ?? string.Empty);
                break;
            case ConversationUpdateType.AssistantCompleted:
                if (update.MessageId is not null && !_completedNotifications.Add(update.ConversationId + "/" + update.MessageId)) break;
                var completedTurn = Turns.FirstOrDefault(item => item.MessageId == update.MessageId);
                if (completedTurn is not null)
                {
                    completedTurn.IsStreaming = false;
                    if (completedTurn.Text.Length == 0)
                    {
                        completedTurn.IsThinkingActive = false;
                    }

                    // The identity belongs to this assistant reply, not to a global
                    // "latest Todo" slot. History turns intentionally remain without
                    // an ID because the persisted chat contract has no such field.
                    completedTurn.CreatedTodoId = update.TodoOutcome == TodoToolCallOutcome.Confirmed
                        && !string.IsNullOrWhiteSpace(update.CreatedTodoId)
                        ? update.CreatedTodoId
                        : null;
                }
                if (completedTurn is not null && update.Expression is { } expression) ExpressionRequested?.Invoke(expression);
                RequestStatusText = string.Empty;
                StopThinkingTimer();
                _pendingReasoning.Clear();
                PendingTodoDraftId = update.TodoDraftId;
                PendingTodoDraftVersion = update.TodoDraftVersion;
                // New C01 flow keeps the structured proposal in session state. The
                // legacy loader remains for old history/card compatibility only.
                if (string.IsNullOrWhiteSpace(update.TodoDraftId))
                {
                    TryLoadTodoProposals(update.StructuredResponse);
                }
                ApplyTodoOutcome(update.TodoOutcome, update.TodoDetail, update.StructuredResponse);
                if (completedTurn is { CanReadAloud: true })
                {
                    AssistantReplyCompleted?.Invoke(completedTurn);
                }
                break;
            case ConversationUpdateType.Cancelled:
                RemoveStreamingTurns();
                StopThinkingTimer();
                _pendingReasoning.Clear();
                ErrorText = "已取消。";
                break;
            case ConversationUpdateType.Failed:
                PreserveFailedStreamingTurns();
                StopThinkingTimer();
                _pendingReasoning.Clear();
                ErrorText = update.SafeError ?? "对话暂时不可用。";
                RequestStatusText = update.HttpStatusCode is { } code ? $"请求失败 · HTTP {code}" : "请求失败";
                break;
        }

        TrimTurns();
    }

    private void StartThinkingTimer()
    {
        if (IsThinking)
        {
            return;
        }

        IsThinking = true;
        _thinkingStartedTicks = Environment.TickCount64;
        _thinkingTimer ??= new System.Timers.Timer(100)
        {
            AutoReset = true,
        };
        _thinkingTimer.Elapsed += UpdateThinkingTimerText;
        _thinkingTimer.Start();
        ThinkingTimerText = "思考 · 0.0s";
    }

    private void UpdateThinkingTimerText(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var seconds = (Environment.TickCount64 - _thinkingStartedTicks) / 1000.0;
        ThinkingTimerText = $"思考 · {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s";
    }

    private void StopThinkingTimer()
    {
        if (!IsThinking)
        {
            return;
        }

        IsThinking = false;
        ThinkingTimerText = string.Empty;
        if (_thinkingTimer is not null)
        {
            _thinkingTimer.Elapsed -= UpdateThinkingTimerText;
            _thinkingTimer.Stop();
        }
    }

    private void RemoveStreamingTurns()
    {
        foreach (var turn in Turns.Where(turn => turn.IsStreaming).ToArray())
        {
            Turns.Remove(turn);
        }
    }

    private void PreserveFailedStreamingTurns()
    {
        foreach (var turn in Turns.Where(turn => turn.IsStreaming).ToArray())
        {
            turn.IsStreaming = false;
            turn.IsThinkingActive = false;
        }
    }

    private void TrimTurns()
    {
        while (Turns.Count > 20)
        {
            Turns.RemoveAt(0);
        }

        OnPropertyChanged(nameof(IsConversationEmpty));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
    }

    public bool TryLoadTodoProposals(string? structuredResponse)
    {
        if (_todoProposals is null || string.IsNullOrWhiteSpace(structuredResponse))
        {
            return false;
        }

        try
        {
            var parsed = _todoProposals.Parse(structuredResponse);
            ClearTodoProposals();
            foreach (var proposal in parsed)
            {
                var viewModel = new TodoProposalViewModel(proposal, _todoProposals);
                viewModel.Closed += OnTodoProposalClosed;
                TodoProposals.Add(viewModel);
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void ClearTodoProposals()
    {
        foreach (var proposal in TodoProposals)
        {
            proposal.Closed -= OnTodoProposalClosed;
        }

        TodoProposals.Clear();
    }

    private void OnTodoProposalClosed(TodoProposalViewModel proposal) { if (proposal.IsRemoved) TodoProposals.Remove(proposal); }

    public void ShowArchiveDraft(ArchiveDraft draft)
    {
        if (_archiveDrafts is null)
        {
            return;
        }

        ArchiveDrafts.Add(new ArchiveDraftViewModel(draft, _archiveDrafts));
    }

    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateSession()
    {
        _sessionGeneration++;
        _acceptRequestUpdates = false;
        _orchestrator.CancelCurrent();
        IsStreaming = false;
        _lastUserMessage = null;
        PendingTodoDraftId = null;
        PendingTodoDraftVersion = null;
        SetTodoNotice(string.Empty, TodoNoticeKind.None);
        RetryTodoCommand.NotifyCanExecuteChanged();
    }

    private void OnShowReasoningChanged(bool value) => OnPropertyChanged(nameof(ShowReasoning));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InvalidateSession();
        _orchestrator.Updated -= OnConversationUpdated;
        SessionContext.PropertyChanged -= OnSessionContextChanged;
        if (_modelConnection is not null)
        {
            _modelConnection.ConnectionSaved -= OnConnectionSaved;
            _modelConnection.ShowReasoningChanged -= OnShowReasoningChanged;
        }
        StopThinkingTimer();
        _thinkingTimer?.Dispose();
    }

    partial void OnActiveServantIdChanged(string value)
    {
        CancelHistoryDeletion();
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        RetryTodoCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RetryTodoCommand.NotifyCanExecuteChanged();
    }
}
