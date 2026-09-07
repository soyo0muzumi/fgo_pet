using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Settings;
using FgoPet.Core.Settings;
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

public sealed partial class ConversationViewModel : ObservableObject
{
    private readonly ConversationOrchestrator _orchestrator;
    private readonly IAppSettingsStore _settings;
    private readonly ModelConnectionViewModel? _modelConnection;
    private readonly TodoProposalService? _todoProposals;
    private readonly ArchiveDraftService? _archiveDrafts;
    private string _activeConversationId = string.Empty;
    private bool _configurationRequired;
    private readonly StringBuilder _pendingReasoning = new();
    private long _thinkingStartedTicks;
    private System.Timers.Timer? _thinkingTimer;

    public ConversationViewModel(
        ConversationOrchestrator orchestrator,
        IAppSettingsStore settings,
        ModelConnectionViewModel? modelConnection = null,
        TodoProposalService? todoProposals = null,
        ArchiveDraftService? archiveDrafts = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _modelConnection = modelConnection;
        _todoProposals = todoProposals;
        _archiveDrafts = archiveDrafts;
        if (_modelConnection is not null)
        {
            _modelConnection.ConnectionSaved += OnConnectionSaved;
            _modelConnection.ShowReasoningChanged += _ => OnPropertyChanged(nameof(ShowReasoning));
        }
        _orchestrator.Updated += OnConversationUpdated;
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
    }

    /// <summary>Raised when the user asks to configure the model; the host owns the settings route.</summary>
    public event Action<SettingsSection>? SettingsRequested;

    public ObservableCollection<ConversationTurnViewModel> Turns { get; } = new();
    public ObservableCollection<TodoProposalViewModel> TodoProposals { get; } = new();
    public ObservableCollection<ArchiveDraftViewModel> ArchiveDrafts { get; } = new();
    public ObservableCollection<ConversationHistoryItem> History { get; } = new();

    [ObservableProperty]
    private string _historyStatus = string.Empty;

    [ObservableProperty]
    private bool _isHistoryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanSendOrStop))]
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
    private bool _isStreaming;

    [ObservableProperty]
    private string _providerStatusText;

    [ObservableProperty]
    private string _modelStatusText;

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _requestStatusText = string.Empty;

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

    public bool CanSend => !IsStreaming
        && !string.IsNullOrWhiteSpace(ActiveServantId)
        && !string.IsNullOrWhiteSpace(InputText);

    public bool CanStop => IsStreaming;

    public bool CanSendOrStop => CanSend || CanStop;

    public string ActionLabel => IsStreaming ? "停止生成" : "发送消息";

    public sealed record TodoNoticeState(string Text, TodoNoticeKind Kind);

    private TodoNoticeState _todoNotice = new(string.Empty, TodoNoticeKind.None);
    private string? _lastUserMessage;

    /// <summary>Raised when the user asks to create a Todo manually; the host owns the route.</summary>
    public event Action? ManualTodoRequested;

    public TodoNoticeState TodoNotice => _todoNotice;

    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand SendOrStopCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand NewConversationCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand RetryTodoCommand { get; }
    public IRelayCommand ManualTodoCommand { get; }
    public IRelayCommand RetryHistoryCommand { get; }

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
        var normalizedServantId = servantId?.Trim() ?? string.Empty;
        if (string.Equals(ActiveServantId, normalizedServantId, StringComparison.Ordinal))
        {
            return;
        }

        _orchestrator.CancelCurrent();
        Turns.Clear();
        StopThinkingTimer();
        _pendingReasoning.Clear();
        ClearTodoProposals();
        ArchiveDrafts.Clear();
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

    public void LoadHistory()
    {
        IsHistoryLoading = true;
        History.Clear();
        if (string.IsNullOrWhiteSpace(ActiveServantId))
        {
            HistoryStatus = "暂无历史对话";
            IsHistoryLoading = false;
            return;
        }

        try
        {
            foreach (var conversation in _orchestrator.ListConversations(ActiveServantId))
            {
                var first = _orchestrator.ListConversationMessages(conversation.ConversationId, ActiveServantId)
                    .FirstOrDefault(message => message.Role == ChatMessageRole.User);
                var title = string.IsNullOrWhiteSpace(first?.Text) ? "新会话" : first.Text;
                History.Add(new ConversationHistoryItem(
                    conversation.ConversationId,
                    title.Length > 50 ? title[..50] : title,
                    conversation.UpdatedAtUtc,
                    conversation.IsArchived ? "已归档" : "正常"));
            }
            HistoryStatus = History.Count == 0 ? "暂无历史对话" : string.Empty;
        }
        catch (Exception)
        {
            HistoryStatus = "历史会话加载失败，请重试。";
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    public void SetActiveConversation(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(ActiveServantId)) return;
        try
        {
            _orchestrator.CancelCurrent();
            var messages = _orchestrator.LoadConversation(conversationId, ActiveServantId);
            Turns.Clear();
            ClearTodoProposals();
            ArchiveDrafts.Clear();
            _activeConversationId = conversationId;
            foreach (var message in messages.Where(m => m.Role is ChatMessageRole.User or ChatMessageRole.Assistant))
            {
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
        ProviderStatusText = connection.ProviderId;
        ModelStatusText = connection.ModelId;
        _configurationRequired = false;
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }


    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (!CanSend || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        InputText = string.Empty;
        await SendCoreAsync(text);
    }

    private async Task SendCoreAsync(string text)
    {
        ErrorText = string.Empty;
        SetTodoNotice(string.Empty, TodoNoticeKind.None);
        StopThinkingTimer();
        _pendingReasoning.Clear();
        IsStreaming = true;
        try
        {
            _lastUserMessage = text;
            var result = await _orchestrator.SendAsync(ActiveServantId, text, CancellationToken.None);
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
                _configurationRequired = false;
                OnPropertyChanged(nameof(IsConfigurationRequired));
                OnPropertyChanged(nameof(IsConfigurationStateVisible));
                OnPropertyChanged(nameof(IsEmptyStateVisible));
            }
        }
        finally
        {
            IsStreaming = false;
        }
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

    private void NewConversation()
    {
        if (string.IsNullOrWhiteSpace(ActiveServantId))
        {
            return;
        }

        _orchestrator.StartNewConversation(ActiveServantId);
        Turns.Clear();
        StopThinkingTimer();
        _pendingReasoning.Clear();
        ClearTodoProposals();
        ArchiveDrafts.Clear();
        _activeConversationId = string.Empty;
        ErrorText = string.Empty;
        _configurationRequired = false;
        OnPropertyChanged(nameof(IsConversationEmpty));
        OnPropertyChanged(nameof(IsEmptyStateVisible));
        OnPropertyChanged(nameof(IsConfigurationRequired));
        OnPropertyChanged(nameof(IsConfigurationStateVisible));
    }

    private void OnConversationUpdated(ConversationUpdate update)
    {
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
            case ConversationUpdateType.RequestStage:
                LastHttpStatusCode = update.HttpStatusCode;
                RequestStatusText = update.RequestStage switch
                {
                    ConversationRequestStage.Preparing => "正在准备请求…",
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
                var completedTurn = Turns.FirstOrDefault(item => item.MessageId == update.MessageId);
                if (completedTurn is not null)
                {
                    completedTurn.IsStreaming = false;
                    if (completedTurn.Text.Length == 0)
                    {
                        completedTurn.IsThinkingActive = false;
                    }
                }
                RequestStatusText = string.Empty;
                StopThinkingTimer();
                _pendingReasoning.Clear();
                TryLoadTodoProposals(update.StructuredResponse);
                ApplyTodoOutcome(update.TodoOutcome, update.TodoDetail, update.StructuredResponse);
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

    private void OnTodoProposalClosed(TodoProposalViewModel proposal) => TodoProposals.Remove(proposal);

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

    partial void OnActiveServantIdChanged(string value)
    {
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
