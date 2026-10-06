using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Infrastructure.Packs;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Dialogue;

public sealed partial class ConversationOrchestrator
{
    private readonly NativeConversationRuntime? _nativeRuntime;
    private readonly IAgentRunStore? _agentRuns;
    private readonly IAgentFinalDeliveryStore? _finalDeliveries;
    private readonly Func<ToolScope, bool>? _nativeScopeCurrent;

    public ValueTask<AgentInteractionSnapshot?> GetAgentInteractionAsync(string runId, ToolScope scope, CancellationToken token) =>
        _nativeRuntime?.GetInteractionAsync(runId, scope, token) ?? ValueTask.FromResult<AgentInteractionSnapshot?>(null);
    public Task<ConversationSendResult> ResumeAgentInputAsync(UserInputReply reply, ToolScope scope, CancellationToken token) =>
        ResumeNativeAsync(cancellation => _nativeRuntime!.ResumeInputAsync(reply, scope, cancellation), token);
    public Task<ConversationSendResult> ResumeAgentApprovalAsync(ApprovalReply reply, ToolScope scope, CancellationToken token) =>
        ResumeNativeAsync(cancellation => _nativeRuntime!.ResumeApprovalAsync(reply, scope, cancellation), token);
    private async Task<ConversationSendResult> ResumeNativeAsync(
        Func<CancellationToken, ValueTask<ConversationSendResult>> resume, CancellationToken token)
    {
        if (_nativeRuntime is null) throw new AgentStateException("RUN_HOST_CLOSED");
        using var lease = _lifetime.Acquire(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        lock (_gate)
        {
            if (_activeCancellation is not null) return new(ConversationSendStatus.Failed, string.Empty, SafeError: "当前已有对话请求正在进行。");
            _activeCancellation = cancellation;
        }
        try { return await resume(cancellation.Token); }
        finally { lock (_gate) { if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null; } }
    }

    private string NativeModelRevision() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(_settings?.Load().ModelConnection is { } connection
            ? connection with { ToolsSupported = true } : null))));

    private async Task<ConversationSendResult> SendNativeAsync(ContentBinding binding, ChatMessage user,
        ToolScope scope, ConversationRequestContext? context, CancellationToken token)
    {
        var sourceReader = new ConversationSourceReader(_conversations);
        var source = sourceReader.Read(scope, user.MessageId, ConversationSourceKind.User);
        var query = new ProtectedQueryReference(user.MessageId, source.Fingerprint);
        var identity = new AgentRunIdentity("run-" + Guid.NewGuid().ToString("N"), user.MessageId, scope,
            NativeModelRevision(), _lifetime.Generation);
        void CheckCurrent()
        {
            if (_nativeScopeCurrent?.Invoke(scope) == false || _lifetime.Generation != identity.AuthorizationRevision || NativeModelRevision() != identity.ModelRevision
                || !sourceReader.IsCurrent(source) || _conversations.GetConversation(scope.ConversationId, scope.RoleId)?.IsArchived != false
                || _conversations.ReadState(ActiveConversationStateKey(scope.RoleId)) != scope.ConversationId)
                throw new AgentStateException("RUN_SCOPE_CHANGED");
        }
        using var preparationLease = _lifetime.Acquire(token);
        CheckCurrent();
        var semanticScope = new ConversationScope(scope.RoleId, scope.ProjectId);
        var recalled = _recall is null ? new ConversationRecallResult(RecallStatus.Empty, [])
            : await _recall.RetrieveAsync(semanticScope, scope.ConversationId, user.Text, preparationLease.Token);
        preparationLease.CheckCurrent(); CheckCurrent();
        if (recalled.Status == RecallStatus.Ambiguous)
            return preparationLease.Commit(() => PersistLocalReply(scope.ConversationId, scope.RoleId, binding.Context,
                recalled.Clarification ?? "你想继续哪一件事？", CapabilityOutcome.None));
        var preparedTurn = (ConversationModelTurn)await ConversationModelTurn.CreateAgentStepAsync(_providerResolver,
            _settings, _contextResolver, _tokenMeter, Publish, CancelCurrent, scope.RoleId, scope.ConversationId, token);
        preparationLease.CheckCurrent(); CheckCurrent();
        ValueTask<IAgentModelStep> CreateModel(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var model = preparedTurn;
            return ValueTask.FromResult<IAgentModelStep>(new PreparedModel(model, async (budget, prepareToken) =>
            {
                using var lease = _lifetime.Acquire(prepareToken);
                CheckCurrent();
                lease.CheckCurrent(); CheckCurrent(); model.EnsureCurrent(lease.Token);
                var sources = recalled.Sources.Where(hit => _conversations.IsCurrentSource(semanticScope, hit)).ToArray();
                if (sources.Length != recalled.Sources.Count) recalled = new(RecallStatus.Unavailable, sources);
                var persona = binding.Persona ?? FallbackPersona(binding.Context);
                ComposedPrompt Compose(ConversationSummary? summary, IReadOnlyList<ChatMessage> messages)
                {
                    lease.CheckCurrent(); CheckCurrent(); model.EnsureCurrent(lease.Token);
                    var currentSources = sources.Where(hit => _conversations.IsCurrentSource(semanticScope, hit)).ToArray();
                    return _composer.Compose(new PromptContext(binding.Context, persona, binding.Knowledge,
                        [], string.Empty, messages.Where(message => message.Status == ChatMessageStatus.Completed && message.MessageId != user.MessageId)
                            .Select(message => new PromptMessage(message.Role, message.Text)).ToArray(), user.Text, context,
                        null, currentSources, currentSources.Length == sources.Length ? recalled.Status : RecallStatus.Unavailable, summary),
                        model.Route, model.Budget, nativeAgent: true);
                }
                var snapshot = _contextStore?.Read(semanticScope, scope.ConversationId);
                var prompt = Compose(snapshot?.Summary, snapshot?.UncoveredMessages ?? _conversations.LoadMessages(scope.ConversationId, scope.RoleId));
                if (_summaries is not null && snapshot is not null && prompt.Usage.InputTokens >= CompactionPlanner.TriggerTokens(model.Budget))
                {
                    Publish(new(ConversationUpdateType.RequestStage, scope.ConversationId, ServantId: scope.RoleId,
                        RequestStage: ConversationRequestStage.Compacting));
                    await _summaries.TryCompactAsync(snapshot, model.Provider, model.Route, model.Budget,
                        prompt.Usage.InputTokens, Compose, lease, () => { CheckCurrent(); model.EnsureCurrent(lease.Token); },
                        new CompactionCallBudget(budget), lease.Token);
                    snapshot = _contextStore!.Read(semanticScope, scope.ConversationId);
                    prompt = Compose(snapshot.Summary, snapshot.UncoveredMessages);
                }
                if (!prompt.FitsBudget) throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
                Publish(new(ConversationUpdateType.HistorySources, scope.ConversationId, ServantId: scope.RoleId,
                    HistorySources: sources.Where(hit => prompt.Messages.Any(message => message.Text.Contains(
                        $"recalled:{hit.Anchor.ConversationId}:{hit.Anchor.MessageId}", StringComparison.Ordinal))).ToArray()));
                return prompt.Messages.Select(message => new ModelMessage(message.Role switch
                {
                    ChatMessageRole.System => ModelMessageRole.System,
                    ChatMessageRole.User => ModelMessageRole.User,
                    _ => ModelMessageRole.Assistant,
                }, message.Text, [])).ToImmutableArray();
            }));
        }
        async ValueTask<ConversationSendResult> Deliver(AgentRunResult result, CancellationToken cancellation)
        {
            var checkpoint = await _agentRuns!.LoadAsync(identity.RunId, cancellation)
                ?? throw new AgentStateException("RUN_STORE_FAILED");
            using var lease = _lifetime.Acquire(cancellation);
            CheckCurrent();
            if (checkpoint.Snapshot.Status != AgentRunStatus.Completed || checkpoint.Snapshot.Identity != identity
                || checkpoint.FinalDeliveryId is null || checkpoint.FinalText is null)
                throw new AgentStateException("RUN_FINAL_INVALID");
            var output = StructuredOutputValidator.Validate(checkpoint.FinalText, ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            var accepted = new AcceptedAgentFinal(identity, checkpoint.FinalDeliveryId, query, binding.Context,
                output, _time.GetUtcNow(), checkpoint.ResolvedInputs);
            var delivery = lease.Commit(() =>
            {
                CheckCurrent();
                if (!_finalDeliveries!.TryAccept(accepted)) throw new AgentStateException("RUN_FINAL_REJECTED");
                return _finalDeliveries.Publish(scope, accepted.DeliveryId) ?? throw new AgentStateException("RUN_FINAL_REJECTED");
            });
            try
            {
                lease.Commit(() =>
                {
                    CheckCurrent();
                    if (_finalDeliveries!.TryClaimObservers(scope, accepted.DeliveryId))
                        _capabilities?.ObserveCompletedTurn(new(scope, user.MessageId, user.Text, delivery.Assistant.MessageId,
                            delivery.Assistant.Text, output.SuggestedFact?.Text, _settings?.Load().ModelConnection, lease.Token));
                });
            }
            catch (Exception error) when (error is not OperationCanceledException and not AgentStateException)
            {
                _logger?.LogWarning("Native post-turn observer dispatch failed: {ErrorCode}", "NATIVE_OBSERVER_DISPATCH_FAILED");
            }
            lease.CheckCurrent(); CheckCurrent();
            Publish(new(ConversationUpdateType.AssistantDelta, scope.ConversationId, delivery.Assistant.MessageId,
                delivery.Assistant.Text, ServantId: scope.RoleId));
            Publish(new(ConversationUpdateType.AssistantCompleted, scope.ConversationId, delivery.Assistant.MessageId,
                delivery.Assistant.Text, ServantId: scope.RoleId, Expression: output.Expression));
            Publish(new(ConversationUpdateType.RequestStage, scope.ConversationId, ServantId: scope.RoleId,
                RequestStage: ConversationRequestStage.Completed));
            return new(ConversationSendStatus.Completed, scope.ConversationId, delivery.Assistant.MessageId);
        }
        var result = await _nativeRuntime!.StartAsync(new(identity, new RunBudget(), [new(ModelMessageRole.User, user.Text, [])], query),
            CreateModel, CheckCurrent, Deliver, token);
        if (result.Status is ConversationSendStatus.Failed or ConversationSendStatus.Cancelled)
        {
            Publish(new(ConversationUpdateType.RequestStage, scope.ConversationId, ServantId: scope.RoleId,
                RequestStage: result.Status == ConversationSendStatus.Cancelled ? ConversationRequestStage.Cancelled : ConversationRequestStage.Failed));
            Publish(new(result.Status == ConversationSendStatus.Cancelled ? ConversationUpdateType.Cancelled : ConversationUpdateType.Failed,
                scope.ConversationId, ServantId: scope.RoleId, SafeError: result.SafeError));
        }
        return result;
    }

    private sealed class PreparedModel(ConversationModelTurn model,
        Func<IModelRequestBudget, CancellationToken, ValueTask<ImmutableArray<ModelMessage>>> prepare)
        : IAgentModelStep, IAgentModelInputBudget, IAgentRunPreparation
    {
        public int InputTokenBudget => model.InputTokenBudget;
        public int MeasureInputTokens(StepEnvironment environment) => model.MeasureInputTokens(environment);
        public ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token) =>
            model.ExecuteAsync(environment, budget, token);
        public ValueTask<ImmutableArray<ModelMessage>> PrepareAsync(ImmutableArray<ModelMessage> initialMessages,
            IModelRequestBudget budget, CancellationToken token) => prepare(budget, token);
    }
}
