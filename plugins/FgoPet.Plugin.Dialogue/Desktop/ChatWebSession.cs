using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;

namespace FgoPet.App.Dialogue;

internal sealed class ChatWebSession : IChatWebSession
{
    private const int MaxSafeTextLength = 12_000;
    private const int MaxAvatarDataUrlLength = 512 * 1024;
    private readonly ConversationViewModel _conversation;
    private readonly IChatWebHostActions _host;
    private readonly Dispatcher _dispatcher;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<HistorySourceViewModel, string> _sourceIds =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConversationTurnViewModel, PropertyChangedEventHandler> _turnSubscriptions =
        new(ReferenceEqualityComparer.Instance);
    private long _version;
    private long _draftRevision;
    private string _latestDraft;
    private long _sessionEpoch;
    private Task? _sendTask;
    private long _sendEpoch = -1;
    private bool _refreshPending;
    private bool _disposed;

    public ChatWebSession(ConversationViewModel conversation, IChatWebHostActions host)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _latestDraft = conversation.InputText;
        _conversation.PropertyChanged += OnConversationPropertyChanged;
        _conversation.SessionChanged += OnConversationSessionChanged;
        _conversation.Turns.CollectionChanged += OnTurnsChanged;
        _conversation.History.CollectionChanged += OnHistoryChanged;
        _conversation.RecalledSources.CollectionChanged += OnSourcesChanged;
        _conversation.SessionContext.PropertyChanged += OnContextChanged;
        _conversation.SessionContext.Chips.CollectionChanged += OnContextChipsChanged;
        _host.Changed += OnHostChanged;
        UpdateTurnSubscriptions();
    }

    public event Action<ChatWebSnapshot>? Changed;

    public ChatWebSnapshot ReadSnapshot()
    {
        if (_dispatcher.CheckAccess()) return BuildSnapshot();
        return _dispatcher.Invoke(BuildSnapshot);
    }

    public ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        if (_disposed) return ValueTask.FromResult(Failure("CHAT_SESSION_CLOSED"));
        if (!ChatWebCommandReader.TryRead(message, out var command, out var errorCode))
            return ValueTask.FromResult(Failure(errorCode));
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<WebSurfaceCommandResult>(cancellationToken);
        if (_dispatcher.CheckAccess()) return new ValueTask<WebSurfaceCommandResult>(HandleOnDispatcherAsync(command!, cancellationToken));
        return new ValueTask<WebSurfaceCommandResult>(_dispatcher.InvokeAsync(
            () => HandleOnDispatcherAsync(command!, cancellationToken), DispatcherPriority.Normal, cancellationToken).Task.Unwrap());
    }

    private async Task<WebSurfaceCommandResult> HandleOnDispatcherAsync(ChatWebCommand command,
        CancellationToken cancellationToken)
    {
        if (_disposed) return Failure("CHAT_SESSION_CLOSED");
        if (command.Type == "chat.get") return Success(BuildSnapshot());
        if (command.Identity is null || !IsCurrent(command.Identity)) return Failure("CHAT_IDENTITY_STALE");
        cancellationToken.ThrowIfCancellationRequested();

        switch (command.Type)
        {
            case "chat.draft":
                if (command.Text!.Length > MaxSafeTextLength) return Failure("CHAT_DRAFT_TOO_LONG");
                if (command.Revision!.Value <= _draftRevision) return Failure("CHAT_DRAFT_STALE");
                _draftRevision = command.Revision.Value;
                _latestDraft = command.Text;
                _conversation.InputText = command.Text;
                ScheduleRefresh();
                return Success(BuildSnapshot());
            case "chat.send":
                return StartGeneration(_conversation.SendCommand, cancellationToken);
            case "chat.stop":
                if (!_conversation.StopCommand.CanExecute(null)) return Failure("CHAT_NOT_AVAILABLE");
                _conversation.StopCommand.Execute(null);
                ScheduleRefresh();
                return Success(BuildSnapshot());
            case "chat.new":
                if (!_conversation.NewConversationCommand.CanExecute(null)) return Failure("CHAT_NOT_AVAILABLE");
                _conversation.NewConversationCommand.Execute(null);
                return Success(BuildSnapshot());
            case "chat.history":
                ApplyHistoryRequest(command.OnlyCurrentProject!.Value, command.Append!.Value);
                return Success(BuildSnapshot());
            case "chat.history.open":
                if (!TryGetHistoryItem(command.TargetConversationId!, out _)) return Failure("CHAT_TARGET_INVALID");
                _conversation.SetActiveConversation(command.TargetConversationId!);
                return Success(BuildSnapshot());
            case "chat.history.requestDelete":
                if (!TryGetHistoryItem(command.TargetConversationId!, out var deleteItem)
                    || !_conversation.CanDeleteHistory) return Failure("CHAT_TARGET_INVALID");
                _conversation.RequestHistoryDeletion(deleteItem);
                return _conversation.PendingHistoryDeletion?.ConversationId == command.TargetConversationId
                    ? Success(BuildSnapshot()) : Failure("CHAT_NOT_AVAILABLE");
            case "chat.history.cancelDelete":
                _conversation.CancelHistoryDeletion();
                return Success(BuildSnapshot());
            case "chat.history.confirmDelete":
                if (_conversation.PendingHistoryDeletion?.ConversationId != command.TargetConversationId
                    || !TryGetHistoryItem(command.TargetConversationId!, out _)
                    || !_conversation.ConfirmHistoryDeletion()) return Failure("CHAT_TARGET_INVALID");
                return Success(BuildSnapshot());
            case "chat.retryCapability":
                return StartGeneration(_conversation.RetryCapabilityCommand, cancellationToken);
            case "chat.host":
                return await HandleHostActionAsync(command, cancellationToken);
            default:
                return Failure("CHAT_COMMAND_INVALID");
        }
    }

    private WebSurfaceCommandResult StartGeneration(IAsyncRelayCommand command, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Failure("CHAT_COMMAND_CANCELED");
        if (_sendTask is { IsCompleted: false } && _sendEpoch == _sessionEpoch)
            return Failure("CHAT_NOT_AVAILABLE");
        if (_conversation.IsStreaming || !command.CanExecute(null)) return Failure("CHAT_NOT_AVAILABLE");

        var epoch = _sessionEpoch;
        var acceptedRevision = _draftRevision;
        var sentDraft = _conversation.InputText;
        var task = command.ExecuteAsync(null);
        _sendTask = task;
        _sendEpoch = epoch;
        _ = ObserveGenerationAsync(task, epoch, acceptedRevision, sentDraft);
        ScheduleRefresh();
        return Success(BuildSnapshot());
    }

    private async Task ObserveGenerationAsync(Task task, long epoch, long acceptedRevision, string sentDraft)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The conversation owner has already converted provider failures to safe view state.
        }

        await RunOnDispatcherAsync(() =>
        {
            if (_disposed) return;
            if (epoch == _sessionEpoch && _draftRevision > acceptedRevision
                && _conversation.InputText.Length == 0 && _latestDraft.Length > 0)
            {
                _conversation.InputText = _latestDraft;
            }
            if (ReferenceEquals(_sendTask, task))
            {
                _sendTask = null;
                _sendEpoch = -1;
            }
            ScheduleRefresh();
        }).ConfigureAwait(false);
    }

    private async Task<WebSurfaceCommandResult> HandleHostActionAsync(ChatWebCommand command,
        CancellationToken cancellationToken)
    {
        var action = command.Action!;
        if (action == "openSource")
        {
            var source = _sourceIds.FirstOrDefault(pair => pair.Value == command.TargetId).Key;
            if (source is null || !_conversation.RecalledSources.Contains(source)
                || !source.OpenCommand.CanExecute(null)) return Failure("CHAT_TARGET_INVALID");
            source.OpenCommand.Execute(null);
            return Success(BuildSnapshot());
        }

        if (action == "removeContextChip")
        {
            var chip = _conversation.SessionContext.Chips.FirstOrDefault(item => item.Id == command.TargetId);
            if (chip is null || !_conversation.SessionContext.TryRemove(chip.Id)) return Failure("CHAT_TARGET_INVALID");
            return Success(BuildSnapshot());
        }

        var presentation = ReadSafePresentation();
        var availability = presentation.Actions ?? new ChatWebHostAvailability();
        var request = ToHostRequest(command);
        if (request is null || !IsHostActionAvailable(action, availability, presentation))
            return Failure("CHAT_NOT_AVAILABLE");
        if (action == "copyTurn" && FindTurn(command.TargetId!)?.Actions.CanCopy != true)
            return Failure("CHAT_TARGET_INVALID");
        if (action == "readTurn" && FindTurn(command.TargetId!)?.CanReadAloud != true)
            return Failure("CHAT_TARGET_INVALID");
        if (action == "openWorkspace" && FindTurn(command.TargetId!)?.CanOpenWorkspace != true)
            return Failure("CHAT_TARGET_INVALID");
        if (action == "selectProject" && !presentation.Projects.Any(choice => choice.Id == command.TargetId && choice.CanSelect))
            return Failure("CHAT_TARGET_INVALID");
        if (action == "selectModel" && !presentation.Models.Any(choice => choice.Id == command.TargetId && choice.CanSelect))
            return Failure("CHAT_TARGET_INVALID");
        var result = await _host.HandleAsync(request, cancellationToken);
        if (result.Success) return Success(BuildSnapshot());
        var safeErrorCode = result.ErrorCode is "CHAT_NOT_AVAILABLE" or "CHAT_TARGET_INVALID"
            or "CHAT_HOST_ACTION_FAILED" or "CHAT_PROJECT_REFRESH_FAILED"
            ? result.ErrorCode
            : "CHAT_HOST_ACTION_FAILED";
        return Failure(safeErrorCode);
    }

    private bool IsHostActionAvailable(string action, ChatWebHostAvailability availability,
        ChatWebHostPresentation presentation) => action switch
    {
        "hide" or "setExpanded" or "copyTurn" or "readTurn" or "openWorkspace" => true,
        "openFocus" => availability.CanOpenFocus,
        "openWorkspaceOverview" => availability.CanOpenWorkspaceOverview,
        "newWorkspaceItem" => availability.CanCreateWorkspaceItem,
        "openPersonalizationSettings" => availability.CanOpenPersonalizationSettings,
        "openSpeechSettings" => availability.CanOpenSpeechSettings,
        "openModelSettings" => availability.CanOpenModelSettings
            && (_conversation.CanOpenModelSettings || _conversation.IsConfigurationRequired),
        "refreshProjects" => availability.CanRefreshProjects && !_conversation.IsStreaming,
        "selectProject" => !_conversation.IsStreaming,
        "selectModel" => !_conversation.IsStreaming,
        _ => false,
    };

    private static ChatWebHostRequest? ToHostRequest(ChatWebCommand command)
    {
        var action = command.Action switch
        {
            "hide" => ChatWebHostAction.Hide,
            "setExpanded" => ChatWebHostAction.SetExpanded,
            "copyTurn" => ChatWebHostAction.CopyTurn,
            "readTurn" => ChatWebHostAction.ReadTurn,
            "openWorkspace" => ChatWebHostAction.OpenWorkspace,
            "openFocus" => ChatWebHostAction.OpenFocus,
            "openWorkspaceOverview" => ChatWebHostAction.OpenWorkspaceOverview,
            "newWorkspaceItem" => ChatWebHostAction.NewWorkspaceItem,
            "openPersonalizationSettings" => ChatWebHostAction.OpenPersonalizationSettings,
            "openSpeechSettings" => ChatWebHostAction.OpenSpeechSettings,
            "openModelSettings" => ChatWebHostAction.OpenModelSettings,
            "refreshProjects" => ChatWebHostAction.RefreshProjects,
            "selectProject" => ChatWebHostAction.SelectProject,
            "selectModel" => ChatWebHostAction.SelectModel,
            _ => (ChatWebHostAction?)null,
        };
        return action is { } value ? new ChatWebHostRequest(value, command.TargetId, command.Expanded) : null;
    }

    private void ApplyHistoryRequest(bool onlyCurrentProject, bool append)
    {
        var filterChanged = _conversation.FilterHistoryByProject != onlyCurrentProject;
        if (filterChanged)
        {
            // The existing setter reloads page one synchronously.
            _conversation.FilterHistoryByProject = onlyCurrentProject;
            return;
        }

        if (append)
        {
            if (_conversation.LoadMoreHistoryCommand.CanExecute(null)) _conversation.LoadMoreHistoryCommand.Execute(null);
        }
        else
        {
            _conversation.LoadHistory();
        }
    }

    private bool TryGetHistoryItem(string id, out ConversationHistoryItem item)
    {
        var matches = _conversation.History.Where(candidate => candidate.ConversationId == id).Take(2).ToArray();
        item = matches.FirstOrDefault()!;
        return matches.Length == 1 && _conversation.ActiveServantId.Length > 0;
    }

    private ConversationTurnViewModel? FindTurn(string id)
    {
        var matches = _conversation.Turns.Where(turn => turn.MessageId == id).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private bool IsCurrent(ChatWebIdentity identity)
    {
        var current = CurrentIdentity();
        return identity.SessionId == current.SessionId
            && identity.ServantId == current.ServantId
            && identity.ConversationId == current.ConversationId;
    }

    private ChatWebIdentity CurrentIdentity() => new(_sessionId,
        _conversation.ActiveServantId ?? string.Empty,
        _conversation.CurrentConversationId ?? string.Empty);

    private ChatWebSnapshot BuildSnapshot()
    {
        SynchronizeIdentity();
        var presentation = ReadSafePresentation();
        var duplicateTurnIds = _conversation.Turns.GroupBy(turn => turn.MessageId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var turns = _conversation.Turns.Select(turn => new ChatWebTurnSnapshot(
            Safe(turn.MessageId, 128),
            turn.Role == ChatMessageRole.User ? "user" : "assistant",
            Safe(turn.Text, MaxSafeTextLength),
            turn.IsStreaming,
            Safe(turn.ReasoningText, MaxSafeTextLength),
            Safe(turn.ReasoningSummary, 256),
            Safe(turn.ReasoningDurationText, 64),
            turn.IsThinkingActive,
            turn.IsReasoningExpanded,
            !duplicateTurnIds.Contains(turn.MessageId) && turn.Actions.CanCopy,
            !duplicateTurnIds.Contains(turn.MessageId) && turn.CanReadAloud,
            !duplicateTurnIds.Contains(turn.MessageId) && turn.CanOpenWorkspace,
            turn.IsSpeechBusy,
            Safe(turn.SpeechActionText, 64),
            Safe(turn.SpeechStatusText, 256),
            turn.SpeechNeedsConfiguration)).ToArray();
        var chips = _conversation.SessionContext.Chips
            .Select(chip => new ChatWebContextChip(Safe(chip.Id, 256), Safe(chip.Label, 320))).ToArray();
        var sources = CurrentSources().Select(source => new ChatWebSource(
            SourceId(source), Safe(source.Title, 160), Safe(source.Detail, 6_000))).ToArray();
        var pendingDelete = _conversation.PendingHistoryDeletion;
        var session = new ChatWebConversationSnapshot(
            turns,
            _conversation.IsStreaming,
            _conversation.IsThinking,
            Safe(_conversation.ThinkingTimerText, 64),
            Safe(_conversation.RequestStatusText, 256),
            Safe(_conversation.ErrorText, 512),
            _conversation.IsConfigurationRequired,
            Safe(_conversation.ProviderStatusText, 120),
            Safe(_conversation.ModelStatusText, 160),
            _conversation.CanOpenModelSettings,
            _conversation.CanSend,
            _conversation.CanStop,
            _conversation.RetryCapabilityCommand.CanExecute(null),
            _conversation.ShowReasoning,
            new ChatWebCapabilityNotice(Safe(_conversation.CapabilityNotice.Text, 512),
                _conversation.CapabilityNotice.Kind.ToString().ToLowerInvariant()),
            new ChatWebProjectContext(Safe(_conversation.SessionContext.ProjectId, 128),
                Safe(_conversation.SessionContext.ProjectLabel, 160)),
            chips,
            sources);
        var historyItems = _conversation.History.GroupBy(item => item.ConversationId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.First())
            .Select(item => new ChatWebHistoryItem(
                Safe(item.ConversationId, 128), Safe(item.Title, 256), Safe(item.UpdatedText, 32), Safe(item.Status, 64))).ToArray();
        var history = new ChatWebHistorySnapshot(
            historyItems,
            Safe(_conversation.HistoryStatus, 256),
            _conversation.IsHistoryLoading,
            _conversation.HasMoreHistory,
            _conversation.FilterHistoryByProject,
            pendingDelete?.ConversationId,
            pendingDelete is null ? null : Safe(pendingDelete.Title, 256),
            _conversation.CanDeleteHistory);
        return new ChatWebSnapshot(CurrentIdentity(), ++_version,
            new ChatWebDraftSnapshot(Safe(_conversation.InputText, MaxSafeTextLength), _draftRevision),
            presentation, session, history);
    }

    private ChatWebHostPresentation ReadSafePresentation()
    {
        ChatWebHostPresentation raw;
        try { raw = _host.ReadPresentation(); }
        catch (Exception) { raw = new("角色", null, [], []); }
        var projects = SanitizeChoices(raw.Projects, _conversation.SessionContext.ProjectId, !_conversation.IsStreaming);
        var modelId = _conversation.ModelStatusText;
        var models = SanitizeChoices(raw.Models, modelId, !_conversation.IsStreaming);
        return new ChatWebHostPresentation(
            Safe(raw.RoleName, 80) is { Length: > 0 } role ? role : "角色",
            SafePngDataUrl(raw.AvatarPngDataUrl),
            projects,
            models,
            raw.IsProjectsLoading,
            Safe(raw.ProjectsStatus, 256),
            raw.Actions ?? new ChatWebHostAvailability());
    }

    private static IReadOnlyList<ChatWebChoice> SanitizeChoices(IReadOnlyList<ChatWebChoice>? choices,
        string selectedId, bool canSelect)
    {
        if (choices is null) return [];
        return choices.Where(choice => choice is not null && !string.IsNullOrWhiteSpace(choice.Id)
                && !string.IsNullOrWhiteSpace(choice.Label))
            .GroupBy(choice => choice.Id.Trim(), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.First())
            .Select(choice => new ChatWebChoice(Safe(choice.Id, 128), Safe(choice.Label, 160),
                choice.Id == selectedId, canSelect && choice.CanSelect))
            .ToArray();
    }

    private static string? SafePngDataUrl(string? value)
    {
        const string prefix = "data:image/png;base64,";
        if (string.IsNullOrEmpty(value) || value.Length > MaxAvatarDataUrlLength
            || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        try
        {
            _ = Convert.FromBase64String(value[prefix.Length..]);
            return value;
        }
        catch (FormatException) { return null; }
    }

    private static string Safe(string? value, int limit) => value is null
        ? string.Empty
        : value.Length <= limit ? value : value[..limit];

    private IEnumerable<HistorySourceViewModel> CurrentSources()
    {
        var current = _conversation.RecalledSources.ToHashSet();
        foreach (var stale in _sourceIds.Keys.Where(source => !current.Contains(source)).ToArray()) _sourceIds.Remove(stale);
        foreach (var source in _conversation.RecalledSources.Distinct())
        {
            _ = SourceId(source);
            yield return source;
        }
    }

    private string SourceId(HistorySourceViewModel source)
    {
        if (_sourceIds.TryGetValue(source, out var id)) return id;
        id = Guid.NewGuid().ToString("N");
        _sourceIds[source] = id;
        return id;
    }

    private void SynchronizeIdentity()
    {
        if (_lastSnapshotServant == _conversation.ActiveServantId
            && _lastSnapshotConversation == _conversation.CurrentConversationId) return;
        _lastSnapshotServant = _conversation.ActiveServantId;
        _lastSnapshotConversation = _conversation.CurrentConversationId;
        _latestDraft = _conversation.InputText;
    }

    private string? _lastSnapshotServant;
    private string? _lastSnapshotConversation;

    private void ScheduleRefresh()
    {
        if (_disposed || _refreshPending) return;
        _refreshPending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshPending = false;
            if (_disposed) return;
            PublishSnapshot();
        }));
    }

    private void PublishSnapshot()
    {
        var snapshot = BuildSnapshot();
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<ChatWebSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch (Exception) { }
        }
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private void UpdateTurnSubscriptions()
    {
        var current = _conversation.Turns.ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var turn in _turnSubscriptions.Keys.Where(turn => !current.Contains(turn)).ToArray())
        {
            turn.PropertyChanged -= _turnSubscriptions[turn];
            _turnSubscriptions.Remove(turn);
        }
        foreach (var turn in _conversation.Turns)
        {
            if (_turnSubscriptions.ContainsKey(turn)) continue;
            PropertyChangedEventHandler handler = (_, _) => ScheduleRefresh();
            _turnSubscriptions.Add(turn, handler);
            turn.PropertyChanged += handler;
        }
    }

    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs args) => ScheduleRefresh();
    private void OnConversationSessionChanged() { _sessionEpoch++; ScheduleRefresh(); }
    private void OnHostChanged(object? sender, EventArgs args) => ScheduleRefresh();
    private void OnContextChanged(object? sender, PropertyChangedEventArgs args) => ScheduleRefresh();
    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs args) { UpdateTurnSubscriptions(); ScheduleRefresh(); }
    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleRefresh();
    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleRefresh();
    private void OnContextChipsChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleRefresh();

    private static WebSurfaceCommandResult Success(ChatWebSnapshot snapshot) =>
        new(true, new { snapshot });

    private static WebSurfaceCommandResult Failure(string errorCode) =>
        new(false, null, errorCode);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conversation.PropertyChanged -= OnConversationPropertyChanged;
        _conversation.SessionChanged -= OnConversationSessionChanged;
        _conversation.Turns.CollectionChanged -= OnTurnsChanged;
        _conversation.History.CollectionChanged -= OnHistoryChanged;
        _conversation.RecalledSources.CollectionChanged -= OnSourcesChanged;
        _conversation.SessionContext.PropertyChanged -= OnContextChanged;
        _conversation.SessionContext.Chips.CollectionChanged -= OnContextChipsChanged;
        _host.Changed -= OnHostChanged;
        foreach (var (turn, handler) in _turnSubscriptions) turn.PropertyChanged -= handler;
        _turnSubscriptions.Clear();
        _sourceIds.Clear();
        Changed = null;
    }
}
