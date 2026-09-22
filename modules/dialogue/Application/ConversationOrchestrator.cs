using System.IO;
using System.Text;
using FgoPet.App.Memory;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Todo;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Memory.Settings;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Dialogue;

public interface IChatProviderResolver
{
    IChatProvider Resolve();
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
}

public sealed class InstalledContentBindingResolver : IConversationContentResolver
{
    private readonly IArtPackageRepository _repository;
    private readonly IAppSettingsStore _settings;

    public InstalledContentBindingResolver(IArtPackageRepository repository, IAppSettingsStore settings)
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
    private readonly SqliteMemoryRepository _memories;
    private readonly PromptComposer _composer;
    private readonly TimeProvider _time;
    private readonly IDialogueSettingsStore? _settings;
    private readonly IMemorySettingsStore? _memorySettings;
    private readonly ConversationSummaryService? _summaries;
    private readonly TodoProposalService? _todoProposals;
    private readonly ILogger<ConversationOrchestrator>? _logger;
    private readonly TodoContinuationState? _todoDrafts;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _conversationIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeCancellation;

    public ConversationOrchestrator(
        IChatProviderResolver providerResolver,
        IConversationContentResolver contentResolver,
        SqliteConversationRepository conversations,
        SqliteMemoryRepository memories,
        PromptComposer composer,
        TimeProvider time,
        IDialogueSettingsStore? settings = null,
        IMemorySettingsStore? memorySettings = null,
        ConversationSummaryService? summaries = null,
        TodoProposalService? todoProposals = null,
        ILogger<ConversationOrchestrator>? logger = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _settings = settings;
        _memorySettings = memorySettings;
        _summaries = summaries;
        _todoProposals = todoProposals;
        _todoDrafts = todoProposals is null ? null : new TodoContinuationState(todoProposals);
        _logger = logger;
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
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            if (binding.Context.ServantId != servantId)
            {
                throw new InvalidDataException("Content binding servant_id does not match the request.");
            }

            contentContext = binding.Context;
            stage = "创建会话";
            conversationId = GetOrCreateConversation(servantId, contentContext);
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
            _conversations.Append(userMessage);
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
                    _todoDrafts.Cancel(conversationId, servantId, pending.DraftId);
                    return PersistLocalTodoReply(conversationId, servantId, contentContext, "好的，这份待办草稿已取消，没有写入待办。", TodoToolCallOutcome.Cancelled);
                }

                if (intent == TodoIntent.Confirm)
                {
                    var committed = _todoDrafts.Confirm(
                        conversationId,
                        servantId,
                        pending.DraftId,
                        pending.Version,
                        $"todo-confirm:{conversationId}:{pending.DraftId}:{pending.Version}");
                    return committed.Kind switch
                    {
                        TodoDraftResultKind.Committed or TodoDraftResultKind.AlreadyCommitted when committed.Todo is not null =>
                            PersistLocalTodoReply(conversationId, servantId, contentContext,
                                FormatCommittedTodoReply(committed.Todos), TodoToolCallOutcome.Confirmed, committed.Todo.Id),
                        TodoDraftResultKind.Unknown => PersistLocalTodoReply(conversationId, servantId, contentContext,
                            "待办写入结果暂时无法确认，草稿已保留，请稍后重试。", TodoToolCallOutcome.CommitUnknown),
                        _ => PersistLocalTodoReply(conversationId, servantId, contentContext,
                            "这份待办草稿已发生变化，请重新确认当前内容。", TodoToolCallOutcome.ConfirmationUnknown),
                    };
                }
            }

            var persona = binding.Persona ?? FallbackPersona(binding.Context);
            stage = "组装提示词";
            var runtimeState = _todoProposals?.BuildRuntimeState(userText) ?? string.Empty;
            if (_todoDrafts?.Get(conversationId, servantId) is { } activeDraft)
            {
                runtimeState = string.IsNullOrWhiteSpace(runtimeState) ? DescribePendingDraft(activeDraft) : runtimeState + "\n" + DescribePendingDraft(activeDraft);
            }
            var prompt = _composer.Compose(new PromptContext(
                binding.Context,
                persona,
                binding.Knowledge,
                IsMemoryEnabled() ? _memories.ListEnabledMemories(servantId) : Array.Empty<StoredMemory>(),
                runtimeState,
                existing.Select(message => new PromptMessage(message.Role, message.Text)).ToArray(),
                userText,
                requestContext));
            var assistantId = "message-" + Guid.NewGuid().ToString("N");

            stage = "发送模型请求";
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.Preparing,
                ServantId: servantId));
            var (responseText, aggregatedCall, sawToolCalls, toolsOffered) = await StreamTurnAsync(prompt, servantId, conversationId, requestCancellation.Token);
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
                        pendingDraft = _todoDrafts?.Replace(conversationId, servantId, todoProposals);
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
                            pendingDraft = _todoDrafts?.Replace(conversationId, servantId, todoProposals);
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
            _conversations.Append(assistant);
            if (output.MemoryCandidate is not null && IsMemoryEnabled())
            {
                _memories.AddCandidate(new MemoryCandidate(
                    "candidate-" + Guid.NewGuid().ToString("N"),
                    servantId,
                    conversationId,
                    output.MemoryCandidate.Text,
                    _time.GetUtcNow(),
                    assistant.MessageId,
                    contentContext.AppearanceId));
            }

            if (_summaries is not null && IsMemoryEnabled())
            {
                try
                {
                    await _summaries.MaybeSummarizeAsync(conversationId, servantId, requestCancellation.Token);
                }
                catch (Exception)
                {
                    // Summary maintenance must not turn a completed dialogue into a failed turn.
                }
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
        CancellationToken cancellationToken)
    {
        var withTools = ShouldOfferTools();
        var (text, call, sawCalls, retryWithoutTools) = await StreamOnceAsync(
            prompt, conversationId, withTools, cancellationToken);
        if (!retryWithoutTools)
        {
            return (text, call, sawCalls, ToolsOffered: withTools);
        }

        MarkToolsUnsupported();
        Publish(new ConversationUpdate(
            ConversationUpdateType.AssistantDelta,
            conversationId,
            null,
            string.Empty,
            SafeError: "当前模型不支持工具箱，已使用文本提案兜底。",
            ServantId: servantId));
        var retry = await StreamOnceAsync(prompt, conversationId, withTools: false, cancellationToken);
        return (retry.Text, retry.ToolCall, retry.SawToolCalls, ToolsOffered: false);
    }

    private async Task<(StringBuilder Text, AggregatedToolCall? ToolCall, bool SawToolCalls, bool RetryWithoutTools)> StreamOnceAsync(
        ComposedPrompt prompt,
        string conversationId,
        bool withTools,
        CancellationToken cancellationToken)
    {
        var servantId = prompt.ContentContext.ServantId;
        var request = withTools
            ? new ChatRequest(servantId, conversationId, prompt.Messages, prompt.ContentContext,
                tools: [TodoToolContracts.CreateSubmitTodoProposals()],
                toolChoice: "auto")
            : new ChatRequest(servantId, conversationId, prompt.Messages, prompt.ContentContext);
        var provider = _providerResolver.Resolve();
        var responseText = new StringBuilder();
        var aggregator = new ToolCallAggregator();
        string? finishReason = null;
        var responseAccepted = false;
        try
        {
            Publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.RequestStarted,
                ServantId: servantId));
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    break;
                }
            }
        }
        catch (ProviderRequestException error) when (withTools && error.Category == ProviderFailureCategory.ToolsRejected)
        {
            return (responseText, null, SawToolCalls: false, RetryWithoutTools: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (responseText, aggregator.Complete(finishReason), aggregator.SawToolCalls, RetryWithoutTools: false);
    }

    private bool ShouldOfferTools() => _settings?.Load().ModelConnection?.ToolsSupported ?? false;

    private void MarkToolsUnsupported()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            var current = _settings.Load();
            var connection = current.ModelConnection;
            if (connection is { ToolsSupported: true })
            {
                _settings.Save(current with
                {
                    ModelConnection = connection with { ToolsSupported = false },
                });
            }
        }
        catch (Exception)
        {
            // A persistence failure must not fail the in-flight dialogue turn.
        }
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

    private static string DescribePendingDraft(PendingTodoDraft draft)
    {
        var proposal = draft.Proposals[0];
        var steps = proposal.StepTitles.Count == 0 ? "无步骤" : string.Join("；", proposal.StepTitles);
        return $"当前待确认 Todo 草稿（第 {draft.Version} 版，尚未创建）：标题={proposal.Title}；步骤={steps}。用户可以修改或明确确认。";
    }

    private enum TodoIntent { Modification, Confirm, Cancel }

    private static TodoIntent ClassifyTodoIntent(string text)
    {
        var normalized = text.Trim().TrimEnd('。', '.', '！', '!', '？', '?', '，', ',');
        if (normalized.Length <= 12 && (normalized.Contains("取消", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("不用", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("算了", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("不创建", StringComparison.OrdinalIgnoreCase)))
        {
            return TodoIntent.Cancel;
        }

        if (normalized.Length <= 16 && (normalized.Equals("确认", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("确定", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("确认创建", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("就这样", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("加入待办", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("好的", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("可以", StringComparison.OrdinalIgnoreCase)))
        {
            return TodoIntent.Confirm;
        }

        return TodoIntent.Modification;
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

    public IReadOnlyList<ChatMessage> ListConversationMessages(string conversationId, string servantId) =>
        _conversations.LoadMessages(conversationId, servantId);

    public IReadOnlyList<ChatMessage> LoadConversation(string conversationId, string servantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        var messages = _conversations.LoadMessages(conversationId, servantId);
        lock (_gate)
        {
            _conversationIds[servantId] = conversationId;
            _conversations.WriteState(ActiveConversationStateKey(servantId), conversationId, _time.GetUtcNow());
        }
        return messages;
    }

    private string GetOrCreateConversation(string servantId, ContentContextKey context)
    {
        lock (_gate)
        {
            if (_conversationIds.TryGetValue(servantId, out var conversationId))
            {
                return conversationId;
            }

            var saved = _conversations.ReadState(ActiveConversationStateKey(servantId));
            if (!string.IsNullOrWhiteSpace(saved))
            {
                try
                {
                    var existing = _conversations.LoadMessages(saved, servantId);
                    _conversationIds[servantId] = saved;
                    return saved;
                }
                catch (Exception)
                {
                    _conversations.DeleteState(ActiveConversationStateKey(servantId));
                }
            }

            conversationId = "conversation-" + Guid.NewGuid().ToString("N");
            _conversations.CreateConversation(conversationId, servantId, context, _time.GetUtcNow());
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

    private bool IsMemoryEnabled() => _memorySettings?.Load().Enabled ?? true;

    private static PersonaBundle FallbackPersona(ContentContextKey context) =>
        new(context.ServantId, context.PackageId, context.PackageVersion, context.PersonaVersion, "保持自然、简洁地回应用户。", []);
}
