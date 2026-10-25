using FgoPet.Extensibility;
using FgoPet.Dialogue.Contracts;
using FgoPet.Kernel.Agent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Settings;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
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

public enum CapabilityNoticeKind
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
    private readonly IConfiguredModelAuthority? _modelAuthority;
    private readonly IConversationHistoryQuery _history;
    private ConversationHistoryCursor? _historyCursor;
    [ObservableProperty]
    private bool _filterHistoryByProject = true;
    partial void OnFilterHistoryByProjectChanged(bool value) => LoadHistory();
    private bool _acceptRequestUpdates;
    private string? _activeRequestId;
    private string _activeConversationId = string.Empty;
    // Composer drafts belong to a conversation and live only for this UI process.
    private readonly Dictionary<(string ServantId, string ConversationId), string> _conversationDrafts = new();
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
    private readonly TimeProvider _time;
    private readonly UserInputBroker _inputValidator;
    private string? _waitingRunId;
    private ToolScope? _waitingScope;
    private long _waitingGeneration;
    private CancellationTokenSource? _inputExpiryCancellation;
    internal UserInputRequest? PendingInput { get; private set; }
    internal ApprovalRequest? PendingApproval { get; private set; }
    internal bool IsApprovalReviewing => _approvalReviewCancellation is not null;
    private CancellationTokenSource? _approvalReviewCancellation;
    internal bool IsInputSubmitting { get; private set; }
    internal string InputErrorText { get; private set; } = string.Empty;
    public bool HasPendingRun => _waitingRunId is not null;

    public ConversationViewModel(
        ConversationOrchestrator orchestrator,
        IDialogueSettingsStore settings,
        ModelConnectionViewModel? modelConnection = null,
        IConversationHistoryQuery? history = null,
        TimeProvider? time = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _modelConnection = modelConnection;
        _history = history ?? orchestrator.HistoryQuery;
        _time = time ?? TimeProvider.System;
        _inputValidator = new UserInputBroker(_time);
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
        StopCommand = new RelayCommand(Stop, () => CanStop);
        NewConversationCommand = new RelayCommand(NewConversation);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        RetryCapabilityCommand = new AsyncRelayCommand(RetryCapabilityAsync, () => CanSendBase);
        NewWorkspaceItemCommand = new RelayCommand(() => NewWorkspaceItemRequested?.Invoke());
        RetryHistoryCommand = new RelayCommand(LoadHistory);
        LoadMoreHistoryCommand = new RelayCommand(() => LoadHistoryPage(append: true), () => HasMoreHistory && !IsHistoryLoading && !_disposed);
    }

    /// <summary>Raised when the user asks to configure the model; the host owns the settings route.</summary>
    public event Action<SettingsSection>? SettingsRequested;

    public ObservableCollection<ConversationTurnViewModel> Turns { get; } = new();
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
    private string? _pendingDraftId;

    [ObservableProperty]
    private int? _pendingDraftVersion;

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
    public bool CanSend => !_disposed && !IsStreaming && !HasPendingRun
        && !_configurationRequired
        && !string.IsNullOrWhiteSpace(ActiveServantId)
        && !string.IsNullOrWhiteSpace(InputText);

    public bool CanStop => IsStreaming || HasPendingRun;

    public bool CanSendOrStop => CanSend || CanStop;

    public string ActionLabel => CanStop ? "停止生成" : "发送消息";

    public bool SelectModelForFutureRequests(string modelId)
    {
        if (IsStreaming || HasPendingRun || string.IsNullOrWhiteSpace(modelId)) return false;
        var current = _settings.Load().ModelConnection;
        if (current is null) return false;
        var normalized = modelId.Trim();
        if (_modelAuthority is null || !_modelAuthority.IsAvailable(normalized)) return false;
        if (string.Equals(current.ModelId, normalized, StringComparison.Ordinal)) return true;
        _settings.Save(_settings.Load() with
        {
            ModelConnection = new ModelConnectionSettings(current.ProviderId, current.BaseUrl, normalized,
                current.ToolsSupported, current.ContextWindowOverride, current.MaxOutputTokens),
        });
        ModelStatusText = normalized;
        ClearWindowSelection();
        return true;
    }

    public IConfiguredModelAuthority? ModelAuthority => _modelAuthority;

    public sealed record CapabilityNoticeState(string Text, CapabilityNoticeKind Kind);

    private CapabilityNoticeState _capabilityNotice = new(string.Empty, CapabilityNoticeKind.None);
    private string? _lastUserMessage;

    /// <summary>Raised when the user asks to create a Todo manually; the host owns the route.</summary>
    public event Action? NewWorkspaceItemRequested;

    /// <summary>Raised only for a newly completed assistant reply in the live session.</summary>
    public event Action<ConversationTurnViewModel>? AssistantReplyCompleted;

    /// <summary>Raised when the active conversation identity is about to change.</summary>
    public event Action? SessionChanged;
    public event Action? RequestCancelled;
    internal event Action<string>? DesktopMessagePersisted;
    private string? _desktopRequestId;
    internal bool IsExternalRequest => _activeRequestId is not null && _desktopRequestId != _activeRequestId;

    public CapabilityNoticeState CapabilityNotice => _capabilityNotice;

    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand SendOrStopCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand NewConversationCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand RetryCapabilityCommand { get; }
    public IRelayCommand NewWorkspaceItemCommand { get; }
    public IRelayCommand RetryHistoryCommand { get; }
    public IRelayCommand LoadMoreHistoryCommand { get; }
    public bool HasMoreHistory => _historyCursor is not null;

    private void SetCapabilityNotice(string text, CapabilityNoticeKind kind)
    {
        _capabilityNotice = new CapabilityNoticeState(text, kind);
        OnPropertyChanged(nameof(CapabilityNotice));
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
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSendOrStop));
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        RetryCapabilityCommand.NotifyCanExecuteChanged();
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

        RememberCurrentDraft();
        InputText = string.Empty;
        InvalidateSession();
        SessionChanged?.Invoke();
        Turns.Clear();
        StopThinkingTimer();
        _pendingReasoning.Clear();


        RestoreProjectContext(null);
        RecalledSources.Clear();
        _activeConversationId = string.Empty;
        ErrorText = string.Empty;
        ActiveServantId = normalizedServantId;
        _configurationRequired = _settings.Load().ModelConnection is null;
        RefreshModelStatus();
        OnPropertyChanged(nameof(IsConversationEmpty));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        RetryCapabilityCommand.NotifyCanExecuteChanged();
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
            RememberCurrentDraft();
            InputText = string.Empty;
            InvalidateSession();
            SessionChanged?.Invoke();
            var messages = _orchestrator.LoadConversation(conversationId, ActiveServantId);
            Turns.Clear();


            RestoreProjectContext(_orchestrator.GetConversation(conversationId, ActiveServantId));
            _activeConversationId = conversationId;
            InputText = _conversationDrafts.GetValueOrDefault((ActiveServantId, conversationId), string.Empty);
            foreach (var message in messages.Where(m => m.Role is ChatMessageRole.User or ChatMessageRole.Assistant))
            {
                if (message.Role == ChatMessageRole.Assistant
                    && message.Status == ChatMessageStatus.Completed
                    && (string.Equals(message.Text, ToolProposalHistoryPlaceholder, StringComparison.Ordinal)
                        || string.Equals(message.Text, "[工具调用]", StringComparison.Ordinal)))
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
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSendOrStop));
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        RetryCapabilityCommand.NotifyCanExecuteChanged();
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
        var result = await SendCoreResultAsync(text, desktopInput: true);
        return result?.Status is ConversationSendStatus.Completed or ConversationSendStatus.WaitingUserInput or ConversationSendStatus.WaitingApproval;
    }

    private async Task<ConversationSendResult?> SendCoreResultAsync(string text, CancellationToken cancellationToken = default,
        bool desktopInput = false)
    {
        if (_disposed) return null;
        var generation = _sessionGeneration;
        var servant = ActiveServantId;
        ErrorText = string.Empty;
        SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
        StopThinkingTimer();
        _pendingReasoning.Clear();
        IsStreaming = true;
        _acceptRequestUpdates = true;
        var requestId = Guid.NewGuid();
        _activeRequestId = "request-" + requestId.ToString("N");
        _desktopRequestId = desktopInput ? _activeRequestId : null;
        try
        {
            _lastUserMessage = text;
            var result = await _orchestrator.SendAsync(
                servant,
                text,
                cancellationToken,
                SessionContext.ToRequestContext(desktopInput ? _windowSelection?.CurrentContent : null), requestId);
            if (!IsCurrent()) return null;
            result = await ApplyInputResultAsync(result);
            if (!IsCurrent()) return null;
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
                if (desktopInput) ClearWindowSelection();
                _configurationRequired = false;
                OnPropertyChanged(nameof(IsConfigurationRequired));
                OnPropertyChanged(nameof(IsConfigurationStateVisible));
                OnPropertyChanged(nameof(IsEmptyStateVisible));
                OnPropertyChanged(nameof(CanSend));
                SendCommand.NotifyCanExecuteChanged();
                SendOrStopCommand.NotifyCanExecuteChanged();
            }

            // The initial user message is already persisted when a run waits.
            // Clear only its accepted draft; later drafts survive input resumption.
            return result;
        }
        finally
        {
            if (IsCurrent()) { IsStreaming = false; _acceptRequestUpdates = false; if (!HasPendingRun) _activeRequestId = null; }
        }
        bool IsCurrent() => !_disposed && generation == _sessionGeneration && servant == ActiveServantId
            && _activeRequestId == "request-" + requestId.ToString("N");
    }

    private void ApplyToolOutcome(CapabilityOutcome outcome, string? detail, string? structuredResponse)
    {
        switch (outcome)
        {
            case CapabilityOutcome.ProposalsReady:
                SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
                break;
            case CapabilityOutcome.InvalidToolCall:
                SetCapabilityNotice($"提案生成失败：{detail ?? "工具调用无法解析。"}", CapabilityNoticeKind.Error);
                break;
            case CapabilityOutcome.NoProposal:
                SetCapabilityNotice("本次未生成提案。可重试生成，或手动创建。", CapabilityNoticeKind.Empty);
                break;
            case CapabilityOutcome.TextFallback:
                SetCapabilityNotice("当前模型不支持工具箱，已使用文本提案兜底。", CapabilityNoticeKind.Fallback);
                break;
            case CapabilityOutcome.Confirmed:
                PendingDraftId = null;
                PendingDraftVersion = null;
                SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
                break;
            case CapabilityOutcome.Cancelled:
                PendingDraftId = null;
                PendingDraftVersion = null;
                SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
                break;
            case CapabilityOutcome.CommitUnknown:
                SetCapabilityNotice("写入结果暂时无法确认，草稿已保留。", CapabilityNoticeKind.Error);
                break;
        }
    }

    private async Task RetryCapabilityAsync()
    {
        var text = _lastUserMessage;
        if (!CanSendBase || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
        await SendCoreAsync(text);
    }

    private bool CanSendBase => !_disposed && !IsStreaming && !HasPendingRun && !_configurationRequired
        && !string.IsNullOrWhiteSpace(ActiveServantId)
        && !string.IsNullOrWhiteSpace(_lastUserMessage);

    private void Stop()
    {
        RequestCancelled?.Invoke();
        if (HasPendingRun && !IsStreaming) { _ = CancelInputWaitAsync("已取消。"); return; }
        _orchestrator.CancelCurrent();
    }

    private ToolScope CurrentInputScope() => new(CurrentConversationId, ActiveServantId,
        string.IsNullOrWhiteSpace(SessionContext.ProjectId) ? null : SessionContext.ProjectId);

    private async Task<ConversationSendResult> ApplyInputResultAsync(ConversationSendResult result)
    {
        if (result.Status is not (ConversationSendStatus.WaitingUserInput or ConversationSendStatus.WaitingApproval))
        {
            ClearInputWait();
            return result;
        }
        var generation = _sessionGeneration;
        var scope = CurrentInputScope();
        AgentInteractionSnapshot? interaction = null;
        try
        {
            if (result.RunId is { } runId) interaction = await _orchestrator.GetAgentInteractionAsync(runId, scope, CancellationToken.None);
        }
        catch (AgentStateException) { }
        if (_disposed || generation != _sessionGeneration || scope != CurrentInputScope()) return result;
        if (interaction is null || result.Status == ConversationSendStatus.WaitingUserInput && interaction.Input is null
            || result.Status == ConversationSendStatus.WaitingApproval && (interaction.Approval is null || !SupportedApproval(interaction.Approval)))
        {
            if (result.RunId is { } runId) await _orchestrator.CancelAgentRunAsync(runId, scope, CancellationToken.None);
            return result with { Status = ConversationSendStatus.Failed, SafeError = "本次问题已不可用，请重新发起请求。" };
        }
        _waitingRunId = result.RunId;
        _waitingScope = scope;
        _waitingGeneration = generation;
        PendingInput = interaction.Input;
        PendingApproval = interaction.Approval;
        InputErrorText = string.Empty;
        StopThinkingTimer();
        RequestStatusText = PendingApproval is not null ? "等待你确认具体操作…" : "等待你的回答…";
        StartInputExpiry();
        NotifyInputWait();
        return result;
    }

    internal bool TryAnswerInput(UserInputReply reply, out string errorCode)
    {
        if (!PrepareInputReply(reply, out errorCode)) return false;
        _ = ResumeInputReplyAsync(reply, _sessionGeneration, _activeRequestId);
        return true;
    }

    private bool PrepareInputReply(UserInputReply reply, out string errorCode)
    {
        if (!MatchInput(reply.RunId, reply.RequestId, reply.ExpectedRevision, out errorCode)) return false;
        try { _inputValidator.Validate(PendingInput!, reply, CurrentInputScope()); }
        catch (AgentStateException error)
        {
            errorCode = error.Code == "RUN_INVALID_ANSWERS" ? "CHAT_INPUT_INVALID" : "CHAT_INPUT_STALE";
            InputErrorText = "请完整回答这些问题，或取消本次任务。";
            NotifyInputWait();
            return false;
        }
        IsInputSubmitting = true;
        CancelInputExpiry();
        IsStreaming = true;
        _acceptRequestUpdates = true;
        InputErrorText = string.Empty;
        NotifyInputWait();
        return true;
    }

    internal bool TryCancelInput(string runId, string requestId, long revision, out string errorCode)
    {
        if (!MatchInput(runId, requestId, revision, out errorCode, allowExpired: true)) return false;
        _ = CancelInputWaitAsync("已取消。");
        return true;
    }

    private bool MatchInput(string runId, string requestId, long revision, out string errorCode, bool allowExpired = false)
    {
        errorCode = "CHAT_INPUT_STALE";
        if (_disposed || IsInputSubmitting || PendingInput is not { } input || _activeRequestId is null
            || _waitingGeneration != _sessionGeneration || _waitingScope != CurrentInputScope()
            || input.Binding.Identity.RunId != runId || input.Binding.RequestId != requestId || input.Binding.WaitingRevision != revision)
            return false;
        if (!allowExpired && _time.GetUtcNow() >= input.Binding.ExpiresAt)
        {
            errorCode = "CHAT_INPUT_EXPIRED";
            return false;
        }
        errorCode = string.Empty;
        return true;
    }

    private static bool SupportedApproval(ApprovalRequest approval) => (approval.Tool.PluginId, approval.Tool.ToolName) switch
    {
        ("reminders", "reminder.create" or "reminder.cancel") =>
            !string.IsNullOrWhiteSpace(approval.Tool.BusinessConfirmation?.PlainTextSummary),
        ("firstparty.focus", "focus.resume") => approval.Tool.Resource is not null,
        _ => false,
    };

    internal ChatWebApproval? ReadApproval() => PendingApproval is { } approval ? new(
        approval.Binding.Identity.RunId, approval.Binding.RequestId, approval.Binding.WaitingRevision,
        approval.Binding.ExpiresAt, approval.Tool.ToolName,
        $"操作 / Tool: {approval.Tool.ToolName}\n插件 / Owner: {approval.Tool.PluginId} {approval.Tool.PluginVersion}\n"
        + (approval.Tool.ToolName == "focus.resume" ? "恢复指定专注会话。不会新建会话或更改计划。\n" : "")
        + (approval.Tool.BusinessConfirmation is { } business
            ? $"{business.PlainTextSummary}\n确认草稿 / Confirmation draft: {business.DraftId}\n确认版本 / Confirmation version: {business.Version}\n" : "")
        + (approval.Tool.Resource is { } resource ? $"资源 / Resource: {resource.Id}\n版本 / Revision: {resource.Revision}\n" : "")
        + $"完整冻结参数 / Frozen arguments:\n{approval.NormalizedArgumentsJson}\n"
        + $"确认有效期 / Expires (UTC): {approval.Binding.ExpiresAt.UtcDateTime:O}",
        IsInputSubmitting || IsApprovalReviewing, InputErrorText)
        {
            ActionTitle = approval.Tool.ToolName switch
            {
                "reminder.create" => "Create reminder", "reminder.cancel" => "Cancel reminder",
                "focus.resume" => "Resume focus session", _ => null,
            },
            PlainTextSummary = approval.Tool.ToolName switch
            {
                "reminder.create" or "reminder.cancel" => approval.Tool.BusinessConfirmation?.PlainTextSummary,
                "focus.resume" when approval.Tool.Resource is { } frozen =>
                    $"Resume this paused focus session, keeping its phase, remaining time and cycle. No new session or plan change.\nSession: {frozen.Id}\nRevision: {frozen.Revision}",
                _ => null,
            },
        } : null;

    private bool MatchApproval(string runId, string requestId, long revision, out string errorCode,
        bool allowExpired = false)
    {
        errorCode = "CHAT_APPROVAL_STALE";
        if (_disposed || IsInputSubmitting || PendingApproval is not { } approval || !SupportedApproval(approval)
            || _activeRequestId is null || _waitingGeneration != _sessionGeneration || _waitingScope != CurrentInputScope()
            || approval.Binding.Identity.RunId != runId || approval.Binding.RequestId != requestId
            || approval.Binding.WaitingRevision != revision) return false;
        if (!allowExpired && _time.GetUtcNow() >= approval.Binding.ExpiresAt)
        { errorCode = "CHAT_APPROVAL_EXPIRED"; return false; }
        errorCode = string.Empty;
        return true;
    }

    internal bool TryCancelApproval(string runId, string requestId, long revision, out string errorCode)
    {
        if (!MatchApproval(runId, requestId, revision, out errorCode, allowExpired: true)) return false;
        _ = CancelInputWaitAsync("已取消，未批准操作。");
        return true;
    }

    internal bool TryDenyApproval(string runId, string requestId, long revision, out string errorCode)
    {
        if (!MatchApproval(runId, requestId, revision, out errorCode)) return false;
        _approvalReviewCancellation?.Cancel();
        ResumeApproval(ApprovalDecision.Deny);
        return true;
    }

    internal async ValueTask<string?> ReviewApprovalAsync(string runId, string requestId, long revision,
        Func<ChatWebApproval, CancellationToken, ValueTask<ChatApprovalDecision>> nativeReview, CancellationToken token)
    {
        if (IsApprovalReviewing) return "CHAT_APPROVAL_BUSY";
        if (!MatchApproval(runId, requestId, revision, out var error)) return error;
        var request = PendingApproval;
        var generation = _sessionGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _approvalReviewCancellation = cancellation;
        NotifyInputWait();
        try
        {
            var decision = await nativeReview(ReadApproval()!, cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != _sessionGeneration || !ReferenceEquals(request, PendingApproval)
                || !MatchApproval(runId, requestId, revision, out error)) return "CHAT_APPROVAL_STALE";
            switch (decision)
            {
                case ChatApprovalDecision.Allow: ResumeApproval(ApprovalDecision.Allow); break;
                case ChatApprovalDecision.Deny: ResumeApproval(ApprovalDecision.Deny); break;
                default: await CancelInputWaitAsync("已取消，未批准操作。"); break;
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return "CHAT_APPROVAL_STALE"; }
        catch (Exception) { return "CHAT_APPROVAL_UNAVAILABLE"; }
        finally
        {
            if (ReferenceEquals(_approvalReviewCancellation, cancellation)) _approvalReviewCancellation = null;
            NotifyInputWait();
        }
    }

    private void ResumeApproval(ApprovalDecision decision)
    {
        var binding = PendingApproval!.Binding;
        var reply = new ApprovalReply(binding.Identity.RunId, binding.RequestId, binding.WaitingRevision, decision);
        IsInputSubmitting = true;
        CancelInputExpiry();
        IsStreaming = true;
        _acceptRequestUpdates = true;
        InputErrorText = string.Empty;
        NotifyInputWait();
        _ = ResumeInteractionAsync(reply.RunId, scope => _orchestrator.ResumeAgentApprovalAsync(reply, scope, CancellationToken.None),
            _sessionGeneration, _activeRequestId);
    }

    private Task<ConversationSendResult?> ResumeInputReplyAsync(UserInputReply reply, long generation, string? requestId,
        CancellationToken cancellationToken = default) =>
        ResumeInteractionAsync(reply.RunId, scope => _orchestrator.ResumeAgentInputAsync(reply, scope, cancellationToken), generation, requestId);

    private async Task<ConversationSendResult?> ResumeInteractionAsync(string runId, Func<ToolScope, Task<ConversationSendResult>> resume,
        long generation, string? requestId)
    {
        bool IsCurrent() => !_disposed && generation == _sessionGeneration && requestId == _activeRequestId;
        var scope = CurrentInputScope();
        try
        {
            var result = await resume(scope);
            if (!IsCurrent()) return null;
            result = await ApplyInputResultAsync(result);
            if (!IsCurrent()) return null;
            if (result.Status == ConversationSendStatus.Failed) ErrorText = result.SafeError ?? "本次任务未完成，请重新发起请求。";
            else if (result.Status == ConversationSendStatus.Cancelled) RequestStatusText = "已取消。";
            return result;
        }
        catch (AgentStateException)
        {
            await RestoreWaitAsync("本次问题已不可用，请重新发起请求。");
        }
        catch (Exception)
        {
            await RestoreWaitAsync("本次回答未能完成，请核对状态后重试或取消。");
        }
        finally
        {
            if (IsCurrent())
            {
                IsStreaming = false;
                IsInputSubmitting = false;
                _acceptRequestUpdates = false;
                if (!HasPendingRun) _activeRequestId = null;
                else StartInputExpiry();
                NotifyInputWait();
            }
        }

        return null;

        async Task RestoreWaitAsync(string error)
        {
            if (!IsCurrent()) return;
            AgentInteractionSnapshot? interaction = null;
            try { interaction = await _orchestrator.GetAgentInteractionAsync(runId, scope, CancellationToken.None); }
            catch (AgentStateException) { /* A rejected scope or registration is no longer actionable. */ }
            catch (Exception)
            {
                // A failed status read does not prove the live wait ended. Keep the stop/retry route.
                if (IsCurrent()) InputErrorText = error;
                return;
            }
            if (!IsCurrent()) return;
            if (interaction is { Snapshot.Status: AgentRunStatus.WaitingUserInput, Input: not null }
                or { Snapshot.Status: AgentRunStatus.WaitingApproval })
            {
                PendingInput = interaction.Input;
                PendingApproval = interaction.Approval is { } approval && SupportedApproval(approval) ? approval : null;
                InputErrorText = error;
                return;
            }
            ClearInputWait();
            ErrorText = error;
            RequestStatusText = "本次任务已结束。";
        }
    }

    private async Task CancelInputWaitAsync(string status)
    {
        if (_waitingRunId is not { } runId || _waitingScope is not { } scope || IsInputSubmitting) return;
        RequestCancelled?.Invoke();
        _approvalReviewCancellation?.Cancel();
        var generation = _sessionGeneration;
        CancelInputExpiry();
        IsInputSubmitting = true;
        NotifyInputWait();
        try
        {
            await _orchestrator.CancelAgentRunAsync(runId, scope, CancellationToken.None);
            if (!_disposed && generation == _sessionGeneration && _waitingRunId == runId)
            {
                ClearInputWait();
                _activeRequestId = null;
                _acceptRequestUpdates = false;
                RequestStatusText = status;
            }
        }
        catch (Exception)
        {
            if (!_disposed && generation == _sessionGeneration)
            {
                IsInputSubmitting = false;
                InputErrorText = "本次任务未能结束，请重试停止。";
                NotifyInputWait();
            }
        }
    }

    private void ClearInputWait()
    {
        ClearExternalOwnership();
        CancelInputExpiry();
        _waitingRunId = null;
        _waitingScope = null;
        PendingInput = null;
        PendingApproval = null;
        _approvalReviewCancellation?.Cancel();
        IsInputSubmitting = false;
        InputErrorText = string.Empty;
        NotifyInputWait();
    }

    private void StartInputExpiry()
    {
        CancelInputExpiry();
        var binding = PendingInput?.Binding ?? PendingApproval?.Binding;
        if (binding is null) return;
        var cancellation = new CancellationTokenSource();
        _inputExpiryCancellation = cancellation;
        _ = ObserveInputExpiryAsync(binding, _sessionGeneration, cancellation);
    }

    private void CancelInputExpiry()
    {
        var cancellation = _inputExpiryCancellation;
        _inputExpiryCancellation = null;
        cancellation?.Cancel();
    }

    private async Task ObserveInputExpiryAsync(InteractionBinding binding, long generation, CancellationTokenSource cancellation)
    {
        try
        {
            var remaining = binding.ExpiresAt - _time.GetUtcNow();
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _time, cancellation.Token);
            if (!cancellation.IsCancellationRequested && !_disposed && generation == _sessionGeneration
                && (PendingInput?.Binding ?? PendingApproval?.Binding) == binding && !IsInputSubmitting)
                await CancelInputWaitAsync("本次确认或问题已过期，请重新发起请求。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_inputExpiryCancellation, cancellation)) _inputExpiryCancellation = null;
            cancellation.Dispose();
        }
    }

    private void NotifyInputWait()
    {
        foreach (var name in new[] { nameof(HasPendingRun), nameof(PendingInput), nameof(PendingApproval), nameof(IsApprovalReviewing), nameof(IsInputSubmitting), nameof(InputErrorText),
            nameof(CanSend), nameof(CanStop), nameof(CanSendOrStop), nameof(ActionLabel) }) OnPropertyChanged(name);
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RetryCapabilityCommand.NotifyCanExecuteChanged();
    }

    private async Task SendOrStopAsync()
    {
        if (CanStop)
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
        _conversationDrafts.Remove((ActiveServantId, item.ConversationId));
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
        RememberCurrentDraft();
        InputText = string.Empty;
        InvalidateSession();
        CancelHistoryDeletion();
        SessionChanged?.Invoke();
        Turns.Clear();
        StopThinkingTimer();
        _pendingReasoning.Clear();


        if (clearContext) RestoreProjectContext(null);
        RecalledSources.Clear();
        _activeConversationId = string.Empty;
        _lastUserMessage = null;
        PendingDraftId = null;
        PendingDraftVersion = null;
        RequestStatusText = string.Empty;
        LastHttpStatusCode = null;
        SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
        RetryCapabilityCommand.NotifyCanExecuteChanged();
        ErrorText = string.Empty;
        OnPropertyChanged(nameof(IsConversationEmpty));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanSendOrStop));
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
    }

    private void RememberCurrentDraft()
    {
        if (string.IsNullOrWhiteSpace(ActiveServantId) || string.IsNullOrWhiteSpace(_activeConversationId)) return;
        var key = (ActiveServantId, _activeConversationId);
        if (string.IsNullOrEmpty(InputText)) _conversationDrafts.Remove(key);
        else _conversationDrafts[key] = InputText;
    }

    private void OnConversationUpdated(ConversationUpdate update)
    {
        if (_disposed || !_acceptRequestUpdates || update.RequestId != _activeRequestId) return;
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
            if (_desktopRequestId == update.RequestId && !string.IsNullOrWhiteSpace(update.MessageId))
                DesktopMessagePersisted?.Invoke(update.MessageId);
            OnPropertyChanged(nameof(CurrentConversationId));
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
                    completedTurn.WorkspaceId = update.WorkspaceId;
                    completedTurn.CreatedItemId = update.ToolOutcome == CapabilityOutcome.Confirmed
                        && !string.IsNullOrWhiteSpace(update.CreatedItemId)
                        ? update.CreatedItemId
                        : null;
                }
                if (completedTurn is not null && update.Expression is { } expression) ExpressionRequested?.Invoke(expression);
                RequestStatusText = string.Empty;
                StopThinkingTimer();
                _pendingReasoning.Clear();
                PendingDraftId = update.DraftId;
                PendingDraftVersion = update.DraftVersion;
                ApplyToolOutcome(update.ToolOutcome, update.CapabilityDetail, update.StructuredResponse);
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

    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
    }

    private void InvalidateSession()
    {
        RequestCancelled?.Invoke();
        _sessionGeneration++;
        _acceptRequestUpdates = false;
        _activeRequestId = null;
        ClearInputWait();
        _orchestrator.CancelCurrent();
        IsStreaming = false;
        _lastUserMessage = null;
        PendingDraftId = null;
        PendingDraftVersion = null;
        SetCapabilityNotice(string.Empty, CapabilityNoticeKind.None);
        RetryCapabilityCommand.NotifyCanExecuteChanged();
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
            (_modelAuthority as IDisposable)?.Dispose();
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
        RetryCapabilityCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RetryCapabilityCommand.NotifyCanExecuteChanged();
    }
}
