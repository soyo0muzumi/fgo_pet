using System.IO;
using System.Text;
using FgoPet.Character.Settings;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Todo;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Memory.Settings;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Dialogue;

public interface IChatProviderResolver
{
    IChatProvider Resolve();
    IChatProvider Resolve(ModelConnectionSettings settings) => Resolve();
}

public interface IConversationContentResolver
{
    Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken);
}

public sealed class ConfiguredChatProviderResolver : IChatProviderResolver
{
    private readonly IDialogueSettingsStore _settings;
    private readonly ChatProviderFactory _factory;

    public ConfiguredChatProviderResolver(IDialogueSettingsStore settings, ChatProviderFactory factory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IChatProvider Resolve()
    {
        var settings = _settings.Load().ModelConnection;
        return settings is null
            ? throw new ProviderRequestException(ProviderFailureCategory.Configuration, "尚未配置模型连接。")
            : _factory.Create(settings);
    }

    public IChatProvider Resolve(ModelConnectionSettings settings) => _factory.Create(settings);
}

public sealed class InstalledContentBindingResolver : IConversationContentResolver
{
    private readonly IArtPackageRepository _repository;
    private readonly ICharacterSettingsStore _settings;

    public InstalledContentBindingResolver(IArtPackageRepository repository, ICharacterSettingsStore settings)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken)
    {
        var catalog = await _repository.ScanAsync(cancellationToken);
        var candidates = catalog.Packs
            .Where(pack => string.Equals(pack.ServantId, servantId, StringComparison.Ordinal))
            .ToArray();
        var selection = _settings.Load().Selection;
        var selected = candidates.FirstOrDefault(pack =>
                selection is not null
                && pack.PackageId == selection.PackageId
                && (selection.PackageVersion is null || pack.PackageVersion == selection.PackageVersion)
                && pack.Appearances.Any(appearance => appearance.AppearanceId == selection.AppearanceId))
            ?? candidates.OrderByDescending(pack => pack.Version).FirstOrDefault();
        if (selected is null)
        {
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "当前从者没有可用角色包。");
        }

        var appearanceId = selected.Appearances.FirstOrDefault(appearance =>
                selection is not null && appearance.AppearanceId == selection.AppearanceId)?.AppearanceId
            ?? selected.Appearances.First().AppearanceId;
        return ContentBindingResolver.Resolve(selected.PackRoot, servantId, appearanceId);
    }
}

public sealed class ConversationOrchestrator
{
    private readonly IChatProviderResolver _providerResolver;
    private readonly IConversationContentResolver _contentResolver;
    private readonly SqliteConversationRepository _conversations;
    private readonly IMemoryRecall _memories;
    private readonly IMemoryCandidateSink? _memoryCandidates;
    private readonly MemoryExtractionQueue? _memoryExtractions;
    private readonly PromptComposer _composer;
    private readonly TimeProvider _time;
    private readonly IDialogueSettingsStore? _settings;
    private readonly IMemorySettingsStore? _memorySettings;
    private readonly ConversationSummaryService? _summaries;
    private readonly TodoProposalService? _todoProposals;
    private readonly ILogger<ConversationOrchestrator>? _logger;
    private readonly ITodoDraftWorkflow? _todoDrafts;
    private readonly IModelContextResolver _contextResolver;
    private readonly IRequestTokenMeter _tokenMeter;
    private readonly DialogueContextLifetime _lifetime;
    private readonly IConversationRecall? _recall;
    private readonly IConversationContextStore? _contextStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _conversationIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeCancellation;

    public ConversationOrchestrator(
        IChatProviderResolver providerResolver,
        IConversationContentResolver contentResolver,
        SqliteConversationRepository conversations,
        IMemoryRecall memories,
        PromptComposer composer,
        TimeProvider time,
        IDialogueSettingsStore? settings = null,
        IMemorySettingsStore? memorySettings = null,
        ConversationSummaryService? summaries = null,
        TodoProposalService? todoProposals = null,
        ILogger<ConversationOrchestrator>? logger = null,
        ITodoDraftWorkflow? todoDrafts = null,
        IModelContextResolver? contextResolver = null,
        IRequestTokenMeter? tokenMeter = null,
        DialogueContextLifetime? lifetime = null,
        IConversationRecall? recall = null,
        IConversationContextStore? contextStore = null,
        IMemoryCandidateSink? memoryCandidates = null,
        MemoryExtractionQueue? memoryExtractions = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _memoryCandidates = memoryCandidates ?? memories as IMemoryCandidateSink;
        _memoryExtractions = memoryExtractions;
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _settings = settings;
        _memorySettings = memorySettings;
        _summaries = summaries;
        _todoProposals = todoProposals;
        _todoDrafts = todoDrafts ?? todoProposals?.Drafts;
        _logger = logger;
        _contextResolver = contextResolver ?? new ModelContextResolver(connection => _providerResolver.Resolve(connection), time);
        _tokenMeter = tokenMeter ?? new RequestTokenMeter();
        _lifetime = lifetime ?? new DialogueContextLifetime();
        _recall = recall;
        _contextStore = contextStore;
    }

    public event Action<ConversationUpdate>? Updated;

    public async Task<ConversationSendResult> SendAsync(
        string servantId,
        string userText,
        CancellationToken cancellationToken,
        ConversationRequestContext? requestContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        using var lease = _lifetime.Acquire(cancellationToken);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        lock (_gate)
        {
            if (_activeCancellation is not null)
            {
                return new ConversationSendResult(ConversationSendStatus.Failed, string.Empty, SafeError: "当前已有对话请求正在进行。 ");
            }

            _activeCancellation = requestCancellation;
        }

        var conversationId = string.Empty;
        ContentContextKey? contentContext = null;
        var stage = "角色包解析";
        try
        {
            var binding = await _contentResolver.ResolveAsync(servantId, requestCancellation.Token);
            requestCancellation.Token.ThrowIfCancellationRequested();
            if (binding.Context.ServantId != servantId)
            {
                throw new InvalidDataException("Content binding servant_id does not match the request.");
            }

            contentContext = binding.Context;
            stage = "创建会话";
            var scope = new ConversationScope(servantId, requestContext?.ProjectId);
            conversationId = lease.Commit(() => GetOrCreateConversation(servantId, contentContext, scope, requestContext?.ProjectLabel));
            stage = "读取会话历史";
            var allMessages = _conversations.LoadMessages(conversationId, servantId).ToArray();
            var existing = allMessages
                .Where(message => message.Status == ChatMessageStatus.Completed)
                .ToArray();
            var now = _time.GetUtcNow();
            var userMessage = new ChatMessage(
                "message-" + Guid.NewGuid().ToString("N"),
                conversationId,
                servantId,
                ChatMessageRole.User,
                userText,
                ChatMessageStatus.Completed,
                now,
                contentContext,
                allMessages.Length + 1);
            stage = "保存用户消息";
            lease.Commit(() => _conversations.Append(userMessage));
            Publish(new ConversationUpdate(
                ConversationUpdateType.UserMessagePersisted,
                conversationId,
                userMessage.MessageId,
                userMessage.Text,
                ServantId: servantId));

            if (_todoDrafts?.Get(conversationId, servantId) is { } pending
                && ClassifyTodoIntent(userText) is var intent
                && intent != TodoIntent.Modification)
            {
                if (intent == TodoIntent.Cancel)
                {
                    lease.Commit(() => _todoDrafts.Cancel(conversationId, servantId, pending.DraftId));
                    return lease.Commit(() => PersistLocalTodoReply(conversationId, servantId, contentContext, "好的，这份待办草稿已取消，没有写入待办。", TodoToolCallOutcome.Cancelled));
                }

                if (intent == TodoIntent.Confirm)
                {
                    var committed = lease.Commit(() => _todoDrafts.Confirm(
                        conversationId,
                        servantId,
                        pending.DraftId,
                        pending.Version,
                        $"todo-confirm:{conversationId}:{pending.DraftId}:{pending.Version}"));
                    return lease.Commit(() => committed.Kind switch
                    {
                        TodoDraftResultKind.Committed or TodoDraftResultKind.AlreadyCommitted when committed.Todo is not null =>
                            PersistLocalTodoReply(conversationId, servantId, contentContext,
                                FormatCommittedTodoReply(committed.Todos), TodoToolCallOutcome.Confirmed, committed.Todo.Id),
                        TodoDraftResultKind.Unknown => PersistLocalTodoReply(conversationId, servantId, contentContext,
                            "待办写入结果暂时无法确认，草稿已保留，请稍后重试。", TodoToolCallOutcome.CommitUnknown),
                        _ => PersistLocalTodoReply(conversationId, servantId, contentContext,
                            "这份待办草稿已发生变化，请重新确认当前内容。", TodoToolCallOutcome.ConfirmationUnknown),
                    });
                }
            }

            var persona = binding.Persona ?? FallbackPersona(binding.Context);
            stage = "组装提示词";
            var session = await PrepareModelAsync(requestCancellation.Token);
            var recalled = _recall is null ? new ConversationRecallResult(RecallStatus.Empty, []) :
                await _recall.RetrieveAsync(scope, conversationId, userText, requestCancellation.Token);
            lease.CheckCurrent();
            EnsureCurrent(session, requestCancellation.Token);
            if (recalled.Status == RecallStatus.Ambiguous)
                return lease.Commit(() => PersistLocalTodoReply(conversationId, servantId, contentContext,
                    recalled.Clarification ?? "你想继续哪一件事？", TodoToolCallOutcome.None));
            var sources = recalled.Sources.Where(source => _conversations.IsCurrentSource(scope, source)).ToArray();
            if (sources.Length != recalled.Sources.Count) recalled = new(RecallStatus.Unavailable, sources);
            ComposedPrompt ComposeCurrent(ConversationSummary? summary, IReadOnlyList<ChatMessage> messages)
            {
                var tools = session.Connection?.ToolsSupported == true && !session.ToolsFallbackUsed
                    ? new[] { TodoToolContracts.CreateSubmitTodoProposals() } : null;
                lease.CheckCurrent();
                EnsureCurrent(session, requestCancellation.Token);
                var currentSources = sources.Where(source => _conversations.IsCurrentSource(scope, source)).ToArray();
                return _composer.Compose(new PromptContext(binding.Context, persona, binding.Knowledge,
                    IsMemoryEnabled() ? _memories.Query(new(servantId, scope.ProjectId), userText).Items : Array.Empty<StoredMemory>(),
                    _todoProposals?.BuildRuntimeState(userText) ?? string.Empty,
                    messages.Where(message => message.Status == ChatMessageStatus.Completed && message.MessageId != userMessage.MessageId)
                        .Select(message => new PromptMessage(message.Role, message.Text)).ToArray(),
                    userText, requestContext, _todoDrafts?.GetPromptState(conversationId, servantId), currentSources,
                    currentSources.Length == sources.Length ? recalled.Status : RecallStatus.Unavailable, summary),
                    session.Route, session.Budget, tools, tools is null ? null : "auto");
            }
            var snapshot = _contextStore?.Read(scope, conversationId);
            var prompt = ComposeCurrent(snapshot?.Summary, snapshot?.UncoveredMessages ?? existing);
            var compactionCalls = new CompactionCallBudget();
            async Task<bool> CompactAsync()
            {
                if (_summaries is null || _contextStore is null) return false;
                snapshot = _contextStore.Read(scope, conversationId);
                var before = prompt.Usage.InputTokens;
                Publish(new(ConversationUpdateType.RequestStage, conversationId, ServantId: servantId,
                    RequestStage: ConversationRequestStage.Compacting));
                var compacted = await _summaries.TryCompactAsync(snapshot, session.Provider, session.Route, session.Budget,
                    before, ComposeCurrent, lease, () => EnsureCurrent(session, requestCancellation.Token),
                    compactionCalls, requestCancellation.Token);
                lease.CheckCurrent();
                snapshot = _contextStore.Read(scope, conversationId);
                prompt = ComposeCurrent(snapshot.Summary, snapshot.UncoveredMessages);
                return compacted && prompt.Usage.InputTokens < before;
            }
            if (prompt.Usage.InputTokens >= CompactionPlanner.TriggerTokens(session.Budget)) await CompactAsync();
            if (!prompt.FitsBudget) throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
            var includedSources = sources.Where(source => prompt.Messages.Any(message => message.Text.Contains(
                $"recalled:{source.Anchor.ConversationId}:{source.Anchor.MessageId}", StringComparison.Ordinal))).ToArray();
            Publish(new(ConversationUpdateType.HistorySources, conversationId, ServantId: servantId, HistorySources: includedSources));
            lease.CheckCurrent();
            var assistantId = "message-" + Guid.NewGuid().ToString("N");

            stage = "发送模型请求";
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.Preparing,
                ServantId: servantId));
            (StringBuilder Text, AggregatedToolCall? ToolCall, bool SawToolCalls, bool ToolsOffered) streamed;
            ComposedPrompt RefreshPrompt()
            {
                var latest = _contextStore?.Read(scope, conversationId);
                return ComposeCurrent(latest?.Summary, latest?.UncoveredMessages ?? _conversations.LoadMessages(conversationId, servantId));
            }
            try { streamed = await StreamTurnAsync(prompt, servantId, conversationId, session, requestCancellation.Token, RefreshPrompt); }
            catch (ProviderRequestException error) when (error.Category == ProviderFailureCategory.ContextLimitExceeded &&
                !session.ContextCompactionRetryUsed && session.PrimaryAttempts < ModelSession.MaxPrimaryAttempts && !session.SawOutput)
            {
                session.ContextCompactionRetryUsed = true;
                // A tool downgrade may have changed both the prompt and its budgeted usage.
                prompt = RefreshPrompt();
                if (!await CompactAsync()) throw;
                if (!prompt.FitsBudget) throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
                streamed = await StreamTurnAsync(prompt, servantId, conversationId, session, requestCancellation.Token, RefreshPrompt);
            }
            var (responseText, aggregatedCall, sawToolCalls, toolsOffered) = streamed;
            var finishOutcome = TodoToolCallOutcome.None;
            string? todoDetail = null;
            IReadOnlyList<TodoProposal>? todoProposals = null;
            string? structuredPayload = null;
            PendingTodoDraft? pendingDraft = null;

            if (aggregatedCall is not null)
            {
                // Tool-call channel: proposals only ever arrive through the tool.
                if (aggregatedCall.TooManyCalls)
                {
                    finishOutcome = TodoToolCallOutcome.InvalidToolCall;
                    todoDetail = "模型同时调用了多个工具调用。";
                }
                else if (string.IsNullOrWhiteSpace(aggregatedCall.CallId))
                {
                    finishOutcome = TodoToolCallOutcome.InvalidToolCall;
                    todoDetail = "工具调用缺少 call_id。";
                }
                else if (!string.Equals(aggregatedCall.Name, TodoToolContracts.SubmitTodoProposalsToolName, StringComparison.Ordinal))
                {
                    finishOutcome = TodoToolCallOutcome.InvalidToolCall;
                    todoDetail = "模型调用了未知的工具。";
                }
                else if (aggregatedCall.TryGetArguments(out var arguments) && _todoProposals is not null)
                {
                    var parsed = _todoProposals.TryParseToolCall(arguments);
                    if (parsed.Success)
                    {
                        todoProposals = parsed.Proposals!;
                        finishOutcome = TodoToolCallOutcome.ProposalsReady;
                        // Tool arguments already use the {todos:[…]} envelope shape the
                        // view model re-parses, so pass them through unchanged.
                        structuredPayload = aggregatedCall.Arguments;
                        pendingDraft = lease.Commit(() => _todoDrafts?.Replace(conversationId, servantId, todoProposals));
                    }
                    else
                    {
                        finishOutcome = TodoToolCallOutcome.InvalidToolCall;
                        todoDetail = DescribeToolCallFailure(parsed);
                    }
                }
                else
                {
                    finishOutcome = TodoToolCallOutcome.InvalidToolCall;
                    todoDetail = "工具参数不是有效的 JSON 对象。";
                }
            }
            else if (sawToolCalls)
            {
                // Fragments streamed but never completed (truncation or a wrong
                // finish reason): never treat half a tool call as plain text.
                finishOutcome = TodoToolCallOutcome.InvalidToolCall;
                todoDetail = "工具调用未完整返回。";
            }
            else if (_todoProposals is not null)
            {
                if (toolsOffered)
                {
                    // Tools were attached but the model replied in plain text without
                    // calling the tool. Ordinary reply stays ordinary; planning intent
                    // yields a visible empty state on the UI side.
                    finishOutcome = TodoToolCallOutcome.NoProposal;
                }
                else
                {
                    // Text-envelope fallback path (tools unsupported for this service).
                    try
                    {
                        todoProposals = _todoProposals.ParseEnvelope(responseText.ToString());
                        if (todoProposals is { Count: > 0 })
                        {
                            finishOutcome = TodoToolCallOutcome.TextFallback;
                            structuredPayload = responseText.ToString();
                            pendingDraft = lease.Commit(() => _todoDrafts?.Replace(conversationId, servantId, todoProposals));
                        }
                    }
                    catch (FormatException)
                    {
                        // Structured proposals are optional and must never fail an ordinary reply.
                        // The degraded mode banner is the visible cue; malformed envelopes stay silent.
                    }
                }
            }

            var rawText = responseText.ToString();
            if (sawToolCalls && todoProposals is { Count: > 0 } && string.IsNullOrWhiteSpace(rawText))
            {
                rawText = BuildPendingDraftReply(todoProposals);
            }
            // Tool-call turns carry the reply in tool arguments, not in message
            // content; an empty text is legal there and must skip text validation.
            var output = sawToolCalls
                ? new ValidatedChatOutput(rawText, ExpressionSemantic.Neutral, null, null)
                : StructuredOutputValidator.Validate(
                    rawText,
                    ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            // ChatMessage requires non-empty text for a Completed message; a
            // tool-call-only turn has no visible text, so persist a placeholder.
            var persistedText = output.Text.Length == 0 && sawToolCalls
                ? "[工具调用：待办提案]"
                : output.Text;
            Publish(new ConversationUpdate(
                ConversationUpdateType.AssistantDelta,
                conversationId,
                assistantId,
                output.Text,
                ServantId: servantId));
            var assistant = new ChatMessage(
                assistantId,
                conversationId,
                servantId,
                ChatMessageRole.Assistant,
                persistedText,
                ChatMessageStatus.Completed,
                _time.GetUtcNow(),
                contentContext,
                _conversations.LoadMessages(conversationId, servantId).Count + 1);
            lease.Commit(() => _conversations.Append(assistant));
            if (_memoryCandidates is not null && IsMemoryEnabled())
            {
                try
                {
                    lease.Commit(() =>
                    {
                        EnsureCurrent(session, requestCancellation.Token);
                        if (!IsMemoryEnabled()) return;
                        if (output.MemoryCandidate is { } candidate)
                        {
                            var ticket = _memoryCandidates.Begin(ReadMemorySource(assistant, scope, MemoryEvidenceKind.AssistantSuggestion));
                            _memoryCandidates.Stage(ticket, [new(candidate.Text)]);
                            _memoryCandidates.Abandon(ticket);
                        }
                        if (_memoryExtractions is not null && session.Connection is { } connection)
                        {
                            var ticket = _memoryCandidates.Begin(ReadMemorySource(userMessage, scope, MemoryEvidenceKind.UserStatement));
                            _memoryExtractions.TryEnqueue(new(ticket, userMessage.Text, assistant.Text, connection, cancellationToken));
                        }
                    });
                }
                catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidOperationException)
                { _logger?.LogWarning("Memory candidate was not scheduled; completed dialogue is retained"); }
            }

            Publish(new ConversationUpdate(
                ConversationUpdateType.AssistantCompleted,
                conversationId,
                assistant.MessageId,
                output.Text,
                ServantId: servantId,
                StructuredResponse: todoProposals is { Count: > 0 } ? structuredPayload : null,
                TodoOutcome: finishOutcome,
                TodoDetail: todoDetail,
                Expression: requestCancellation.IsCancellationRequested ? null : output.Expression,
                TodoDraftId: pendingDraft?.DraftId,
                TodoDraftVersion: pendingDraft?.Version));
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.Completed,
                ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Completed, conversationId, assistant.MessageId);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.Cancelled,
                ServantId: servantId));
            Publish(new ConversationUpdate(ConversationUpdateType.Cancelled, conversationId, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Cancelled, conversationId);
        }
        catch (ProviderRequestException error)
        {
            var safeError = error.Message;
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.Failed,
                HttpStatusCode: error.HttpStatusCode is { } httpStatus ? (int)httpStatus : null,
                ProviderErrorCode: error.ProviderCode,
                ServantId: servantId));
            Publish(new ConversationUpdate(
                ConversationUpdateType.Failed,
                conversationId,
                SafeError: safeError,
                ServantId: servantId,
                HttpStatusCode: error.HttpStatusCode is { } statusCode ? (int)statusCode : null,
                ProviderErrorCode: error.ProviderCode));
            var status = error.Category == ProviderFailureCategory.Configuration
                ? ConversationSendStatus.ConfigurationRequired
                : ConversationSendStatus.Failed;
            return new ConversationSendResult(status, conversationId, SafeError: safeError);
        }
        catch (PromptBudgetException error)
        {
            var safeError = error.Failure switch
            {
                PromptBudgetFailure.PendingDraftTooLarge => "待确认草稿过大，请取消草稿后分批规划。",
                PromptBudgetFailure.UserInputTooLarge => "本次输入过长，请缩短后重试。",
                _ => "当前内容超出模型可用上下文，且无法安全压缩。原记录已保留，请缩短输入或调整模型容量后重试。",
            };
            _logger?.LogWarning("Dialogue prompt budget rejected: {Failure}", error.Failure);
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        catch (InvalidDataException)
        {
            const string safeError = "当前角色包内容不可用，请检查角色包后重试。";
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(
                ConversationUpdateType.Failed,
                conversationId,
                SafeError: safeError,
                ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        catch (IOException)
        {
            const string safeError = "本地对话存储暂时不可用，请重试。";
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        catch (FormatException)
        {
            const string safeError = "模型返回格式无法识别。";
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        catch (Exception error)
        {
            var safeError = GetStageError(stage);
            _logger?.LogError(error, "Dialogue turn failed during {Stage}; conversationId={ConversationId}; provider request may not have started.", stage, conversationId);
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, requestCancellation))
                {
                    _activeCancellation = null;
                }
            }
        }
    }

    public void CancelCurrent()
    {
        _lifetime.Invalidate();
        lock (_gate)
        {
            _activeCancellation?.Cancel();
        }
    }

    /// <summary>
    /// Streams one turn. When tools are enabled for the connection they are sent
    /// with the request; a rejected tools parameter degrades this turn to a
    /// plain-text retry, persists the per-connection downgrade, and keeps the
    /// text-envelope fallback path alive.
    /// </summary>
    private async Task<(StringBuilder Text, AggregatedToolCall? ToolCall, bool SawToolCalls, bool ToolsOffered)> StreamTurnAsync(
        ComposedPrompt prompt,
        string servantId,
        string conversationId,
        ModelSession session,
        CancellationToken cancellationToken,
        Func<ComposedPrompt> refreshPrompt)
    {
        var withTools = session.Connection?.ToolsSupported == true && !session.ToolsFallbackUsed;
        var (text, call, sawCalls, retryWithoutTools) = await StreamOnceAsync(
            prompt, conversationId, withTools, session, cancellationToken);
        if (!retryWithoutTools)
        {
            return (text, call, sawCalls, ToolsOffered: withTools);
        }

        EnsureCurrent(session, cancellationToken);
        session.ToolsFallbackUsed = true;
        MarkToolsUnsupported(session);
        Publish(new ConversationUpdate(
            ConversationUpdateType.AssistantDelta,
            conversationId,
            null,
            string.Empty,
            SafeError: "当前模型不支持工具箱，已使用文本提案兜底。",
            ServantId: servantId));
        var retry = await StreamOnceAsync(refreshPrompt(), conversationId, false, session, cancellationToken);
        return (retry.Text, retry.ToolCall, retry.SawToolCalls, ToolsOffered: false);
    }

    private async Task<(StringBuilder Text, AggregatedToolCall? ToolCall, bool SawToolCalls, bool RetryWithoutTools)> StreamOnceAsync(
        ComposedPrompt prompt,
        string conversationId,
        bool withTools,
        ModelSession session,
        CancellationToken cancellationToken)
    {
        var servantId = prompt.ContentContext.ServantId;
        var request = withTools
            ? new ChatRequest(servantId, conversationId, prompt.Messages, prompt.ContentContext,
                tools: [TodoToolContracts.CreateSubmitTodoProposals()],
                toolChoice: "auto", maxOutputTokens: prompt.MaxOutputTokens)
            : new ChatRequest(servantId, conversationId, prompt.Messages, prompt.ContentContext, maxOutputTokens: prompt.MaxOutputTokens);
        EnsureCurrent(session, cancellationToken);
        if (_tokenMeter.Measure(session.Route, request).InputTokens > session.Budget.InputTokens)
            throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
        var provider = session.Provider;
        var responseText = new StringBuilder();
        var aggregator = new ToolCallAggregator();
        string? finishReason = null;
        var responseAccepted = false;
        var sawOutput = false;
        ChatUsage? completedUsage = null;
        var completed = false;
        try
        {
            if (session.PrimaryAttempts >= ModelSession.MaxPrimaryAttempts)
                throw new ProviderRequestException(ProviderFailureCategory.ServiceUnavailable, "本轮重试次数已用完，请稍后重试。");
            session.PrimaryAttempts++;
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.RequestStarted,
                ServantId: servantId));
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCurrent(session, cancellationToken);
                sawOutput |= !string.IsNullOrEmpty(chunk.TextDelta) || chunk.ReasoningDelta is not null || chunk.ToolCallDelta is not null;
                session.SawOutput |= sawOutput;
                if (chunk.Usage is not null) completedUsage = chunk.Usage;
                if (!responseAccepted)
                {
                    responseAccepted = true;
                    Publish(new ConversationUpdate(
                        ConversationUpdateType.RequestStage,
                        conversationId,
                        RequestStage: ConversationRequestStage.ResponseHeadersReceived,
                        ServantId: servantId));
                }
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    responseText.Append(chunk.TextDelta);
                }

                if (chunk.ReasoningDelta is not null)
                {
                    Publish(new ConversationUpdate(
                        ConversationUpdateType.RequestStage,
                        conversationId,
                        RequestStage: ConversationRequestStage.StreamingReasoning,
                        ServantId: servantId));
                    Publish(new ConversationUpdate(
                        ConversationUpdateType.AssistantDelta,
                        conversationId,
                        null,
                        string.Empty,
                        ReasoningDelta: chunk.ReasoningDelta,
                        ServantId: servantId));
                }

                if (chunk.ToolCallDelta is not null)
                {
                    Publish(new ConversationUpdate(
                        ConversationUpdateType.RequestStage,
                        conversationId,
                        RequestStage: ConversationRequestStage.StreamingTool,
                        ServantId: servantId));
                    aggregator.Add(chunk.ToolCallDelta);
                }

                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    Publish(new ConversationUpdate(
                        ConversationUpdateType.RequestStage,
                        conversationId,
                        RequestStage: ConversationRequestStage.StreamingAnswer,
                        ServantId: servantId));
                }

                if (chunk.FinishReason is not null)
                {
                    finishReason = chunk.FinishReason;
                }

                if (chunk.IsComplete)
                {
                    completed = true;
                    break;
                }
            }
        }
        catch (ProviderRequestException error) when (withTools && !session.SawOutput && !session.ToolsFallbackUsed &&
            session.PrimaryAttempts < ModelSession.MaxPrimaryAttempts && error.Category == ProviderFailureCategory.ToolsRejected)
        {
            return (responseText, null, SawToolCalls: false, RetryWithoutTools: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (completed && completedUsage is not null) _tokenMeter.RecordUsage(session.Route, request, completedUsage);
        if (finishReason == "length" && responseText.Length == 0 && !aggregator.SawToolCalls)
            throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse,
                "模型已耗尽输出额度，尚未返回正文。请在模型设置中提高最大输出 token 数后重试。");
        return (responseText, aggregator.Complete(finishReason), aggregator.SawToolCalls, RetryWithoutTools: false);
    }

    private void MarkToolsUnsupported(ModelSession session)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            var current = _settings.Load();
            var connection = current.ModelConnection;
            if (connection is { ToolsSupported: true } && connection == session.Connection)
            {
                var updated = connection with { ToolsSupported = false };
                _settings.Save(current with
                {
                    ModelConnection = updated,
                });
                session.Connection = updated;
                session.Route = ModelRouteKey.From(updated);
            }
        }
        catch (Exception)
        {
            // A persistence failure must not fail the in-flight dialogue turn.
        }
    }

    private sealed class ModelSession(IChatProvider provider, ModelConnectionSettings? connection, ModelRouteKey route, PromptBudget budget)
    {
        // One initial request, one tool downgrade and one context recovery, in either order.
        public const int MaxPrimaryAttempts = 3;
        public IChatProvider Provider { get; } = provider;
        public ModelConnectionSettings? Connection { get; set; } = connection;
        public ModelRouteKey Route { get; set; } = route;
        public PromptBudget Budget { get; } = budget;
        public int PrimaryAttempts { get; set; }
        public bool SawOutput { get; set; }
        public bool ToolsFallbackUsed { get; set; }
        public bool ContextCompactionRetryUsed { get; set; }
    }

    private async Task<ModelSession> PrepareModelAsync(CancellationToken cancellationToken)
    {
        var connection = _settings?.Load().ModelConnection;
        var provider = connection is null ? _providerResolver.Resolve() : _providerResolver.Resolve(connection);
        var route = connection is null ? new ModelRouteKey(provider.ProviderId, "unconfigured", provider.ModelId, "default") : ModelRouteKey.From(connection);
        var limit = connection is null
            ? new ModelContextLimit(route, 8192, null, ContextLimitSource.ConservativeFallback, "fallback-v1")
            : await _contextResolver.ResolveAsync(connection, cancellationToken);
        var session = new ModelSession(provider, connection, route, PromptBudget.Resolve(limit, connection?.MaxOutputTokens ?? 2048));
        EnsureCurrent(session, cancellationToken);
        return session;
    }

    private void EnsureCurrent(ModelSession session, CancellationToken cancellationToken)
    {
        if (_settings is not null && _settings.Load().ModelConnection != session.Connection)
        {
            _tokenMeter.Invalidate(session.Route);
            CancelCurrent();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string DescribeToolCallFailure(ToolCallProposalResult parsed) => parsed.Failure switch
    {
        TodoToolCallFailure.MissingTodos => "工具参数缺少 todos 数组。",
        TodoToolCallFailure.TooMany => "提案超过 10 条上限。",
        TodoToolCallFailure.NotPlanning => "提案内容被安全校验拒绝。",
        TodoToolCallFailure.UnsupportedField => string.IsNullOrWhiteSpace(parsed.FieldName)
            ? "提案包含不支持的执行字段。"
            : $"提案包含不支持的执行字段：{parsed.FieldName}。",
        _ => "工具调用无法解析。",
    };

    private static string BuildPendingDraftReply(IReadOnlyList<TodoProposal> proposals)
    {
        var lines = proposals.Select((proposal, index) =>
        {
            var steps = proposal.StepTitles.Count == 0
                ? "无步骤"
                : string.Join("、", proposal.StepTitles.Select((title, stepIndex) => $"{stepIndex + 1}. {title}"));
            return $"{index + 1}. {proposal.Title}（步骤：{steps}）";
        });
        return "我整理了以下待办草稿：\n"
            + string.Join("\n", lines)
            + "\n尚未创建；你可以继续修改，确认后才会加入待办。";
    }

    private static string FormatCommittedTodoReply(IReadOnlyList<TodoItem> todos)
    {
        if (todos.Count == 1)
        {
            var todo = todos[0];
            return $"已创建待办“{todo.Title}”，包含 {todo.Steps.Count} 个步骤。";
        }

        return $"已创建 {todos.Count} 个待办：\n"
            + string.Join("\n", todos.Select((todo, index) =>
                $"{index + 1}. “{todo.Title}”（{todo.Steps.Count} 个步骤）"));
    }

    private enum TodoIntent { Modification, Confirm, Cancel }

    private static TodoIntent ClassifyTodoIntent(string text)
    {
        // Authorization must be the entire utterance. Questions, quotations,
        // negations, amendments and vague agreement remain non-writing dialogue.
        var normalized = text.Trim().TrimEnd('。', '.', '！', '!');
        return normalized switch
        {
            "取消" or "取消草稿" or "取消待办" or "不创建" or "不用了" or "算了" => TodoIntent.Cancel,
            "确认" or "确认创建" or "确定创建" or "加入待办" or "确认加入待办" or "确认全部创建" => TodoIntent.Confirm,
            _ => TodoIntent.Modification,
        };
    }

    private ConversationSendResult PersistLocalTodoReply(
        string conversationId,
        string servantId,
        ContentContextKey? context,
        string reply,
        TodoToolCallOutcome outcome,
        string? createdTodoId = null)
    {
        var messageId = "message-" + Guid.NewGuid().ToString("N");
        _conversations.Append(new ChatMessage(
            messageId,
            conversationId,
            servantId,
            ChatMessageRole.Assistant,
            reply,
            ChatMessageStatus.Completed,
            _time.GetUtcNow(),
            context ?? throw new InvalidOperationException("Conversation context is unavailable."),
            _conversations.LoadMessages(conversationId, servantId).Count + 1));
        Publish(new ConversationUpdate(ConversationUpdateType.AssistantDelta, conversationId, messageId, reply, ServantId: servantId));
        Publish(new ConversationUpdate(ConversationUpdateType.AssistantCompleted, conversationId, messageId, reply,
            ServantId: servantId, TodoOutcome: outcome, CreatedTodoId: createdTodoId));
        Publish(new ConversationUpdate(ConversationUpdateType.RequestStage, conversationId, RequestStage: ConversationRequestStage.Completed, ServantId: servantId));
        return new ConversationSendResult(ConversationSendStatus.Completed, conversationId, messageId);
    }

    public void StartNewConversation(string servantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        CancelCurrent();
        lock (_gate)
        {
            _conversationIds.Remove(servantId);
            _conversations.DeleteState(ActiveConversationStateKey(servantId));
            _todoDrafts?.ClearServant(servantId);
        }
    }

    private static string GetStageError(string stage) => stage switch
    {
        "角色包解析" => "对话初始化失败：角色包内容不可用，请检查当前角色包后重试。",
        "创建会话" or "读取会话历史" or "保存用户消息" => "对话初始化失败：本地会话存储暂时不可用，请重试。",
        "组装提示词" => "对话初始化失败：角色包对话内容无法准备，请检查角色包后重试。",
        "发送模型请求" => "对话服务暂时不可用：请检查模型连接设置后重试。",
        _ => "对话服务暂时不可用，请重试。"
    };

    public IReadOnlyList<Conversation> ListConversations(string servantId) =>
        _conversations.ListConversations(servantId);

    public IConversationHistoryQuery HistoryQuery => _conversations;
    public Conversation? GetConversation(string conversationId, string servantId) => _conversations.GetConversation(conversationId, servantId);

    public bool CanOpenSource(ConversationScope scope, HistoryHit source) => _conversations.IsCurrentSource(scope, source);

    public IReadOnlyList<ChatMessage> ListConversationMessages(string conversationId, string servantId) =>
        _conversations.LoadMessages(conversationId, servantId);

    public bool TryDeleteConversation(string conversationId, string servantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        lock (_gate)
        {
            // A cancelled request can still be unwinding and persisting its final
            // state. Wait until it releases the request slot before deleting.
            if (_activeCancellation is not null) return false;
            if (!_conversations.Exists(conversationId, servantId)) return false;
            var stateKey = ActiveConversationStateKey(servantId);
            _conversations.DeleteConversation(conversationId, servantId, stateKey);
            if (_conversationIds.GetValueOrDefault(servantId) == conversationId)
                _conversationIds.Remove(servantId);
            _todoDrafts?.Cancel(conversationId, servantId);
            _logger?.LogDebug("Conversation history deletion completed");
            return true;
        }
    }

    public IReadOnlyList<ChatMessage> LoadConversation(string conversationId, string servantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        CancelCurrent();
        var messages = _conversations.LoadMessages(conversationId, servantId);
        lock (_gate)
        {
            _conversationIds[servantId] = conversationId;
            _conversations.WriteState(ActiveConversationStateKey(servantId), conversationId, _time.GetUtcNow());
        }
        return messages;
    }

    private string GetOrCreateConversation(string servantId, ContentContextKey context, ConversationScope scope, string? projectLabel)
    {
        lock (_gate)
        {
            if (_conversationIds.TryGetValue(servantId, out var conversationId))
            {
                if (_conversations.GetConversation(conversationId, servantId)?.ProjectId == scope.ProjectId &&
                    _conversations.Exists(conversationId, servantId)) return conversationId;
                _conversationIds.Remove(servantId);
            }

            var saved = _conversations.ReadState(ActiveConversationStateKey(servantId));
            if (!string.IsNullOrWhiteSpace(saved))
            {
                try
                {
                    var existing = _conversations.GetConversation(saved, servantId);
                    if (existing is not null && existing.ProjectId == scope.ProjectId)
                    {
                        _conversationIds[servantId] = saved;
                        return saved;
                    }
                }
                catch (Exception)
                {
                    _conversations.DeleteState(ActiveConversationStateKey(servantId));
                }
            }

            conversationId = "conversation-" + Guid.NewGuid().ToString("N");
            _conversations.CreateConversation(conversationId, servantId, context, _time.GetUtcNow(), scope.ProjectId, projectLabel);
            _conversationIds[servantId] = conversationId;
            _conversations.WriteState(ActiveConversationStateKey(servantId), conversationId, _time.GetUtcNow());
            return conversationId;
        }
    }

    private static string ActiveConversationStateKey(string servantId) => $"LastActiveConversationId:{servantId}";

    private void TryAppendFailedMessage(string conversationId, string servantId, ContentContextKey? context)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || context is null)
        {
            return;
        }

        try
        {
            _conversations.Append(new ChatMessage(
                "message-" + Guid.NewGuid().ToString("N"),
                conversationId,
                servantId,
                ChatMessageRole.Assistant,
                string.Empty,
                ChatMessageStatus.Failed,
                _time.GetUtcNow(),
                context,
                _conversations.LoadMessages(conversationId, servantId).Count + 1));
        }
        catch (Exception)
        {
            // A secondary persistence failure must not replace the safe provider error.
        }
    }

    private void Publish(ConversationUpdate update)
    {
        try
        {
            Updated?.Invoke(update);
        }
        catch (Exception)
        {
            // UI observers are not allowed to break persistence or cancellation.
        }
    }

    private MemorySource ReadMemorySource(ChatMessage message, ConversationScope scope, MemoryEvidenceKind kind)
    {
        return new MemoryExtractionSourceReader(_conversations).Read(scope, message.ConversationId, message.MessageId, kind);
    }
    private bool IsMemoryEnabled() => _memorySettings?.Load().Enabled ?? true;

    private static PersonaBundle FallbackPersona(ContentContextKey context) =>
        new(context.ServantId, context.PackageId, context.PackageVersion, context.PersonaVersion, "保持自然、简洁地回应用户。", []);
}
