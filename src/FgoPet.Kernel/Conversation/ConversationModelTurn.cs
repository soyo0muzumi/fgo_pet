using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Conversation;

namespace FgoPet.App.Dialogue;

/// <summary>One provider turn owns streaming, bounded retries and connection-downgrade state.</summary>
internal sealed class ConversationModelTurn
{
    private const int MaxPrimaryAttempts = 3;
    private readonly IDialogueSettingsStore? _settings;
    private readonly IRequestTokenMeter _tokenMeter;
    private readonly ConversationCapabilityRouter? _capabilities;
    private readonly Action<ConversationUpdate> _publish;
    private readonly Action _cancelCurrent;
    public IChatProvider Provider { get; }
    public ModelConnectionSettings? Connection { get; private set; }
    public ModelRouteKey Route { get; private set; }
    public PromptBudget Budget { get; }
    private int PrimaryAttempts { get; set; }
    private bool SawOutput { get; set; }
    public bool ToolsFallbackUsed { get; private set; }
    public bool ContextCompactionRetryUsed { get; set; }
    public bool CanRecoverContextLimit => !ContextCompactionRetryUsed && PrimaryAttempts < MaxPrimaryAttempts && !SawOutput;

    private ConversationModelTurn(IChatProvider provider, ModelConnectionSettings? connection,
        ModelRouteKey route, PromptBudget budget, IDialogueSettingsStore? settings, IRequestTokenMeter meter,
        ConversationCapabilityRouter? capabilities, Action<ConversationUpdate> publish, Action cancelCurrent)
    {
        Provider = provider; Connection = connection; Route = route; Budget = budget;
        _settings = settings; _tokenMeter = meter; _capabilities = capabilities;
        _publish = publish; _cancelCurrent = cancelCurrent;
    }

    public static async Task<ConversationModelTurn> CreateAsync(IChatProviderResolver providerResolver,
        IDialogueSettingsStore? settings, IModelContextResolver contextResolver, IRequestTokenMeter meter,
        ConversationCapabilityRouter? capabilities, Action<ConversationUpdate> publish, Action cancelCurrent,
        CancellationToken cancellationToken)
    {
        var connection = settings?.Load().ModelConnection;
        var provider = connection is null ? providerResolver.Resolve() : providerResolver.Resolve(connection);
        var route = connection is null ? new ModelRouteKey(provider.ProviderId, "unconfigured", provider.ModelId, "default") : ModelRouteKey.From(connection);
        var limit = connection is null
            ? new ModelContextLimit(route, 8192, null, ContextLimitSource.ConservativeFallback, "fallback-v1")
            : await contextResolver.ResolveAsync(connection, cancellationToken);
        var session = new ConversationModelTurn(provider, connection, route, PromptBudget.Resolve(limit, connection?.MaxOutputTokens ?? 2048), settings, meter, capabilities, publish, cancelCurrent);
        session.EnsureCurrent(cancellationToken);
        return session;
    }

    public void EnsureCurrent(CancellationToken cancellationToken)
    {
        if (_settings is not null && _settings.Load().ModelConnection != Connection)
        {
            _tokenMeter.Invalidate(Route);
            _cancelCurrent();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Streams one turn. When tools are enabled for the connection they are sent
    /// with the request; a rejected tools parameter degrades this turn to a
    /// plain-text retry, persists the per-connection downgrade, and keeps the
    /// text-envelope fallback path alive.
    /// </summary>
    public async Task<(StringBuilder Text, AggregatedToolCall? ToolCall, bool SawToolCalls, bool ToolsOffered)> StreamAsync(
        ComposedPrompt prompt,
        string servantId,
        string conversationId,
        CancellationToken cancellationToken,
        Func<ComposedPrompt> refreshPrompt)
    {
        var withTools = Connection?.ToolsSupported == true && !ToolsFallbackUsed && _capabilities?.Tools.Length > 0;
        var (text, call, sawCalls, retryWithoutTools) = await StreamOnceAsync(
            prompt, conversationId, withTools, cancellationToken);
        if (!retryWithoutTools)
        {
            return (text, call, sawCalls, ToolsOffered: withTools);
        }

        EnsureCurrent(cancellationToken);
        ToolsFallbackUsed = true;
        MarkToolsUnsupported();
        _publish(new ConversationUpdate(
            ConversationUpdateType.AssistantDelta,
            conversationId,
            null,
            string.Empty,
            SafeError: "当前模型不支持工具箱，已使用文本提案兜底。",
            ServantId: servantId));
        var retry = await StreamOnceAsync(refreshPrompt(), conversationId, false, cancellationToken);
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
                tools: _capabilities?.Tools.Select(tool => tool.ToChatDefinition()).ToArray(),
                toolChoice: "auto", maxOutputTokens: prompt.MaxOutputTokens)
            : new ChatRequest(servantId, conversationId, prompt.Messages, prompt.ContentContext, maxOutputTokens: prompt.MaxOutputTokens);
        EnsureCurrent(cancellationToken);
        if (_tokenMeter.Measure(Route, request).InputTokens > Budget.InputTokens)
            throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
        var provider = Provider;
        var responseText = new StringBuilder();
        var aggregator = new ToolCallAggregator();
        string? finishReason = null;
        var responseAccepted = false;
        var sawOutput = false;
        ChatUsage? completedUsage = null;
        var completed = false;
        try
        {
            if (PrimaryAttempts >= MaxPrimaryAttempts)
                throw new ProviderRequestException(ProviderFailureCategory.ServiceUnavailable, "本轮重试次数已用完，请稍后重试。");
            PrimaryAttempts++;
            _publish(new ConversationUpdate(
                ConversationUpdateType.RequestStage,
                conversationId,
                RequestStage: ConversationRequestStage.RequestStarted,
                ServantId: servantId));
            await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCurrent(cancellationToken);
                sawOutput |= !string.IsNullOrEmpty(chunk.TextDelta) || chunk.ReasoningDelta is not null || chunk.ToolCallDelta is not null;
                SawOutput |= sawOutput;
                if (chunk.Usage is not null) completedUsage = chunk.Usage;
                if (!responseAccepted)
                {
                    responseAccepted = true;
                    _publish(new ConversationUpdate(
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
                    _publish(new ConversationUpdate(
                        ConversationUpdateType.RequestStage,
                        conversationId,
                        RequestStage: ConversationRequestStage.StreamingReasoning,
                        ServantId: servantId));
                    _publish(new ConversationUpdate(
                        ConversationUpdateType.AssistantDelta,
                        conversationId,
                        null,
                        string.Empty,
                        ReasoningDelta: chunk.ReasoningDelta,
                        ServantId: servantId));
                }

                if (chunk.ToolCallDelta is not null)
                {
                    _publish(new ConversationUpdate(
                        ConversationUpdateType.RequestStage,
                        conversationId,
                        RequestStage: ConversationRequestStage.StreamingTool,
                        ServantId: servantId));
                    aggregator.Add(chunk.ToolCallDelta);
                }

                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    _publish(new ConversationUpdate(
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
        catch (ProviderRequestException error) when (withTools && !SawOutput && !ToolsFallbackUsed &&
            PrimaryAttempts < MaxPrimaryAttempts && error.Category == ProviderFailureCategory.ToolsRejected)
        {
            return (responseText, null, SawToolCalls: false, RetryWithoutTools: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (completed && completedUsage is not null) _tokenMeter.RecordUsage(Route, request, completedUsage);
        if (finishReason == "length" && responseText.Length == 0 && !aggregator.SawToolCalls)
            throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse,
                "模型已耗尽输出额度，尚未返回正文。请在模型设置中提高最大输出 token 数后重试。");
        return (responseText, aggregator.Complete(finishReason), aggregator.SawToolCalls, RetryWithoutTools: false);
    }

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
            if (connection is { ToolsSupported: true } && connection == Connection)
            {
                var updated = connection with { ToolsSupported = false };
                _settings.Save(current with
                {
                    ModelConnection = updated,
                });
                Connection = updated;
                Route = ModelRouteKey.From(updated);
            }
        }
        catch (Exception)
        {
            // A persistence failure must not fail the in-flight dialogue turn.
        }
    }

}
