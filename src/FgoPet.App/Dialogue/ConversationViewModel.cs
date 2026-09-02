using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.Core.Dialogue;
using FgoPet.App.Settings;
using FgoPet.Core.Settings;
using FgoPet.App.ViewModels;
using FgoPet.App.Archives;

namespace FgoPet.App.Dialogue;

public sealed partial class ConversationViewModel : ObservableObject
{
    private readonly ConversationOrchestrator _orchestrator;
    private readonly IAppSettingsStore _settings;
    private readonly ModelConnectionViewModel? _modelConnection;
    private readonly TodoProposalService? _todoProposals;
    private readonly ArchiveDraftService? _archiveDrafts;
    private string _activeConversationId = string.Empty;
    private bool _configurationRequired;

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
    }

    /// <summary>Raised when the user asks to configure the model; the host owns the settings route.</summary>
    public event Action<SettingsSection>? SettingsRequested;

    public ObservableCollection<ConversationTurnViewModel> Turns { get; } = new();
    public ObservableCollection<TodoProposalViewModel> TodoProposals { get; } = new();
    public ObservableCollection<ArchiveDraftViewModel> ArchiveDrafts { get; } = new();

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

    public bool CanSend => !IsStreaming
        && !string.IsNullOrWhiteSpace(ActiveServantId)
        && !string.IsNullOrWhiteSpace(InputText);

    public bool CanStop => IsStreaming;

    public bool CanSendOrStop => CanSend || CanStop;

    public string ActionLabel => IsStreaming ? "停止生成" : "发送消息";

    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand SendOrStopCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand NewConversationCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }

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
        ErrorText = string.Empty;
        IsStreaming = true;
        try
        {
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
            Turns.Add(new ConversationTurnViewModel(
                update.MessageId ?? "user",
                ChatMessageRole.User,
                update.TextDelta ?? string.Empty));
            TrimTurns();
            return;
        }

        if (string.IsNullOrEmpty(_activeConversationId) || update.ConversationId != _activeConversationId)
        {
            return;
        }

        switch (update.Type)
        {
            case ConversationUpdateType.AssistantDelta:
                var turn = Turns.FirstOrDefault(item => item.MessageId == update.MessageId);
                if (turn is null)
                {
                    turn = new ConversationTurnViewModel(update.MessageId ?? "assistant", ChatMessageRole.Assistant, string.Empty, true);
                    Turns.Add(turn);
                }

                turn.Append(update.TextDelta ?? string.Empty);
                break;
            case ConversationUpdateType.AssistantCompleted:
                var completedTurn = Turns.FirstOrDefault(item => item.MessageId == update.MessageId);
                if (completedTurn is not null)
                {
                    completedTurn.IsStreaming = false;
                }
                TryLoadTodoProposals(update.StructuredResponse);
                break;
            case ConversationUpdateType.Cancelled:
                RemoveStreamingTurns();
                ErrorText = "已取消。";
                break;
            case ConversationUpdateType.Failed:
                RemoveStreamingTurns();
                ErrorText = update.SafeError ?? "对话暂时不可用。";
                break;
        }

        TrimTurns();
    }

    private void RemoveStreamingTurns()
    {
        foreach (var turn in Turns.Where(turn => turn.IsStreaming).ToArray())
        {
            Turns.Remove(turn);
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
    }

    partial void OnIsStreamingChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SendOrStopCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
