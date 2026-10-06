using FgoPet.Extensibility;
using FgoPet.Kernel.Conversation;
using System.IO;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Providers;
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

public sealed partial class ConversationOrchestrator
{
    private readonly IChatProviderResolver _providerResolver;
    private readonly IConversationContentResolver _contentResolver;
    private readonly IConversationStore _conversations;
    private readonly PromptComposer _composer;
    private readonly TimeProvider _time;
    private readonly IDialogueSettingsStore? _settings;
    private readonly ConversationSummaryService? _summaries;
    private readonly ConversationCapabilityRouter? _capabilities;
    private readonly ILogger<ConversationOrchestrator>? _logger;
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
        IConversationStore conversations,
        PromptComposer composer,
        TimeProvider time,
        IDialogueSettingsStore? settings = null,
        ConversationSummaryService? summaries = null,
        ConversationCapabilityRouter? capabilities = null,
        ILogger<ConversationOrchestrator>? logger = null,
        IModelContextResolver? contextResolver = null,
        IRequestTokenMeter? tokenMeter = null,
        DialogueContextLifetime? lifetime = null,
        IConversationRecall? recall = null,
        IConversationContextStore? contextStore = null,
        NativeConversationRuntime? nativeRuntime = null,
        FgoPet.Kernel.Agent.IAgentRunStore? agentRuns = null,
        IAgentFinalDeliveryStore? finalDeliveries = null,
        Func<ToolScope, bool>? nativeScopeCurrent = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _settings = settings;
        _summaries = summaries;
        _capabilities = capabilities;
        _logger = logger;
        _contextResolver = contextResolver ?? new ModelContextResolver(connection => _providerResolver.Resolve(connection), time);
        _tokenMeter = tokenMeter ?? new RequestTokenMeter();
        _lifetime = lifetime ?? new DialogueContextLifetime();
        _recall = recall;
        _contextStore = contextStore;
        _nativeRuntime = nativeRuntime;
        _agentRuns = agentRuns;
        _finalDeliveries = finalDeliveries;
        _nativeScopeCurrent = nativeScopeCurrent;
        if (nativeRuntime is not null && (agentRuns is null || finalDeliveries is null))
            throw new ArgumentException("Native runtime requires durable run and final delivery owners.");
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
            if (_nativeRuntime?.HasActiveConversation(conversationId) == true)
                return new(ConversationSendStatus.Failed, conversationId, SafeError: "当前任务仍在等待处理，请先完成或停止该任务。");
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

            var capabilityScope = new ToolScope(conversationId, servantId, scope.ProjectId);
            var local = lease.Commit(() => _capabilities?.TryHandleInput(capabilityScope, userText));
            if (local?.Reply is { } localReply)
                return lease.Commit(() => PersistLocalReply(conversationId, servantId, contentContext,
                    localReply, local.Outcome, local.CreatedItemId, local.WorkspaceId));
            if (_nativeRuntime is not null)
            {
                stage = "执行原生任务";
                return await SendNativeAsync(binding, userMessage, capabilityScope, requestContext, requestCancellation.Token);
            }
            var persona = binding.Persona ?? FallbackPersona(binding.Context);
            stage = "组装提示词";
            var session = await ConversationModelTurn.CreateAsync(_providerResolver, _settings, _contextResolver, _tokenMeter,
                _capabilities, Publish, CancelCurrent, requestCancellation.Token);
            var recalled = _recall is null ? new ConversationRecallResult(RecallStatus.Empty, []) :
                await _recall.RetrieveAsync(scope, conversationId, userText, requestCancellation.Token);
            lease.CheckCurrent();
            session.EnsureCurrent(requestCancellation.Token);
            if (recalled.Status == RecallStatus.Ambiguous)
                return lease.Commit(() => PersistLocalReply(conversationId, servantId, contentContext,
                    recalled.Clarification ?? "你想继续哪一件事？", CapabilityOutcome.None));
            var sources = recalled.Sources.Where(source => _conversations.IsCurrentSource(scope, source)).ToArray();
            if (sources.Length != recalled.Sources.Count) recalled = new(RecallStatus.Unavailable, sources);
            ComposedPrompt ComposeCurrent(ConversationSummary? summary, IReadOnlyList<ChatMessage> messages)
            {
                var tools = session.Connection?.ToolsSupported == true && !session.ToolsFallbackUsed
                    ? _capabilities?.Tools.Select(tool => tool.ToChatDefinition()).ToArray() : null;
                if (tools is { Length: 0 }) tools = null;
                lease.CheckCurrent();
                session.EnsureCurrent(requestCancellation.Token);
                var currentSources = sources.Where(source => _conversations.IsCurrentSource(scope, source)).ToArray();
                return _composer.Compose(new PromptContext(binding.Context, persona, binding.Knowledge,
                    _capabilities?.BuildPrompt(capabilityScope, userText, tools is { Length: > 0 }) ?? [],
                    _capabilities?.BuildContext(capabilityScope, userText) ?? string.Empty,
                    messages.Where(message => message.Status == ChatMessageStatus.Completed && message.MessageId != userMessage.MessageId)
                        .Select(message => new PromptMessage(message.Role, message.Text)).ToArray(),
                    userText, requestContext, _capabilities?.GetPromptState(capabilityScope), currentSources,
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
                    before, ComposeCurrent, lease, () => session.EnsureCurrent(requestCancellation.Token),
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
            try { streamed = await session.StreamAsync(prompt, servantId, conversationId, requestCancellation.Token, RefreshPrompt); }
            catch (ProviderRequestException error) when (error.Category == ProviderFailureCategory.ContextLimitExceeded &&
                session.CanRecoverContextLimit)
            {
                session.ContextCompactionRetryUsed = true;
                // A tool downgrade may have changed both the prompt and its budgeted usage.
                prompt = RefreshPrompt();
                if (!await CompactAsync()) throw;
                if (!prompt.FitsBudget) throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
                streamed = await session.StreamAsync(prompt, servantId, conversationId, requestCancellation.Token, RefreshPrompt);
            }
            var (responseText, aggregatedCall, sawToolCalls, toolsOffered) = streamed;
            ConversationContributionResult? contribution = null;
            if (aggregatedCall is not null)
            {
                if (aggregatedCall.TooManyCalls)
                    contribution = new(CapabilityOutcome.InvalidToolCall, Detail: "模型同时调用了多个工具调用。");
                else if (string.IsNullOrWhiteSpace(aggregatedCall.CallId))
                    contribution = new(CapabilityOutcome.InvalidToolCall, Detail: "工具调用缺少 call_id。");
                else if (aggregatedCall.TryGetArguments(out var arguments) && _capabilities is not null)
                {
                    var result = await lease.Commit(() => _capabilities.InvokeAsync(aggregatedCall.Name,
                        new(capabilityScope, arguments), requestCancellation.Token));
                    lease.CheckCurrent();
                    contribution = result.Conversation ?? new(result.Success ? CapabilityOutcome.None : CapabilityOutcome.InvalidToolCall,
                        Detail: result.ErrorCode == "TOOL_UNAVAILABLE" ? "模型调用了未知的工具。" : result.Success ? null : "工具调用无法解析。");
                }
                else contribution = new(CapabilityOutcome.InvalidToolCall, Detail: "工具参数不是有效的 JSON 对象。");
            }
            else if (sawToolCalls)
                contribution = new(CapabilityOutcome.InvalidToolCall, Detail: "工具调用未完整返回。");
            else if (toolsOffered)
                contribution = new(CapabilityOutcome.NoProposal);
            else
                contribution = lease.Commit(() => _capabilities?.TryInterpretReply(capabilityScope, responseText.ToString()));

            var rawText = responseText.ToString();
            // Tool-call turns carry the reply in tool arguments, not in message
            // content; an empty text is legal there and must skip text validation.
            var output = sawToolCalls
                ? new ValidatedChatOutput(rawText, ExpressionSemantic.Neutral, null, null)
                : StructuredOutputValidator.Validate(
                    rawText,
                    ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            if (contribution?.Reply is { } capabilityReply)
                output = output with { Text = capabilityReply };
            // ChatMessage requires non-empty text for a Completed message; a
            // tool-call-only turn has no visible text, so persist a placeholder.
            var persistedText = output.Text.Length == 0 && sawToolCalls
                ? "[工具调用]"
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
            lease.Commit(() =>
            {
                session.EnsureCurrent(requestCancellation.Token);
                _capabilities?.ObserveCompletedTurn(new(capabilityScope, userMessage.MessageId, userMessage.Text,
                    assistant.MessageId, assistant.Text, output.SuggestedFact?.Text, session.Connection, cancellationToken));
            });

            Publish(new ConversationUpdate(
                ConversationUpdateType.AssistantCompleted,
                conversationId,
                assistant.MessageId,
                output.Text,
                ServantId: servantId,
                StructuredResponse: contribution?.StructuredPayload,
                ToolOutcome: contribution?.Outcome ?? CapabilityOutcome.None,
                CapabilityDetail: contribution?.Detail,
                Expression: requestCancellation.IsCancellationRequested ? null : output.Expression,
                DraftId: contribution?.DraftId,
                DraftVersion: contribution?.DraftVersion, WorkspaceId: contribution?.WorkspaceId));
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
        catch (FgoPet.Kernel.Agent.AgentStateException error) when (_nativeRuntime is not null)
        {
            var safeError = error.Code switch
            {
                "RUN_HOST_CLOSED" => "模型任务暂不可用，本地功能仍可使用。",
                "RUN_STATE_QUOTA_EXCEEDED" => "任务存储已达上限，请先通过历史记录入口清理不需要的对话。",
                _ => "任务结果未能安全保存或确认，请先核对实际状态，再决定是否重试。",
            };
            Publish(new(ConversationUpdateType.RequestStage, conversationId, ServantId: servantId,
                RequestStage: ConversationRequestStage.Failed));
            Publish(new(ConversationUpdateType.Failed, conversationId, ServantId: servantId, SafeError: safeError));
            return new(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
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
        _nativeRuntime?.CancelAll();
        lock (_gate)
        {
            _activeCancellation?.Cancel();
        }
    }

    private ConversationSendResult PersistLocalReply(
        string conversationId,
        string servantId,
        ContentContextKey? context,
        string reply,
        CapabilityOutcome outcome,
        string? createdItemId = null, string? workspaceId = null)
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
            ServantId: servantId, ToolOutcome: outcome, CreatedItemId: createdItemId, WorkspaceId: workspaceId));
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
            _capabilities?.ClearRole(servantId);
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
            _nativeRuntime?.CancelConversation(conversationId);
            var stateKey = ActiveConversationStateKey(servantId);
            _conversations.DeleteConversation(conversationId, servantId, stateKey);
            if (_conversationIds.GetValueOrDefault(servantId) == conversationId)
                _conversationIds.Remove(servantId);
            _capabilities?.RemoveConversation(new(conversationId, servantId, null));
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

    private static PersonaBundle FallbackPersona(ContentContextKey context) =>
        new(context.ServantId, context.PackageId, context.PackageVersion, context.PersonaVersion, "保持自然、简洁地回应用户。", []);
}
