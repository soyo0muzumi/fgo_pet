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
    public ValueTask<bool> CancelAgentRunAsync(string runId, ToolScope scope, CancellationToken token) =>
        _nativeRuntime?.CancelAsync(runId, scope, token) ?? ValueTask.FromResult(false);
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
        ToolScope scope, ConversationRequestContext? context, Action<ConversationUpdate> publish, CancellationToken token)
    {
        void Publish(ConversationUpdate update) => publish(update);
        var sourceReader = new ConversationSourceReader(_conversations);
        var source = sourceReader.Read(scope, user.MessageId, ConversationSourceKind.User);
        var query = new ProtectedQueryReference(user.MessageId, source.Fingerprint);
        var identity = new AgentRunIdentity("run-" + Guid.NewGuid().ToString("N"), user.MessageId, scope,
            NativeModelRevision(), _lifetime.Generation);
        void CheckCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (identity is not null && NativeModelRevision() != identity.ModelRevision)
            {
                // Match ConversationModelTurn.EnsureCurrent regardless of which fence observes the change first.
                CancelCurrent();
                throw new OperationCanceledException("Model connection changed.", token);
            }
            if (identity is null || sourceReader is null || source is null
                || _nativeScopeCurrent?.Invoke(scope) == false || _lifetime.Generation != identity.AuthorizationRevision
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
                recalled.Clarification ?? "你想继续哪一件事？", CapabilityOutcome.None, Publish));
        var preparedTurn = (ConversationModelTurn)await ConversationModelTurn.CreateAgentStepAsync(_providerResolver,
            _settings, _contextResolver, _tokenMeter, Publish, CancelCurrent, scope.RoleId, scope.ConversationId, token);
        preparedTurn.RetainsToolHistory = true;
        preparedTurn.NativeRequestDiagnostics = (environment, budget) =>
            ConversationContextDiagnostics.Record(_logger, identity.RunId,
                budget is AgentRunState state ? state.Snapshot.StepNumber : 0,
                "dispatch", environment, preparedTurn.MeasureProjectedInputTokens);
        preparationLease.CheckCurrent(); CheckCurrent();
        var rawHistory = (_contextStore?.Read(semanticScope, scope.ConversationId).UncoveredMessages
            ?? _conversations.LoadMessages(scope.ConversationId, scope.RoleId))
            .Where(message => message.MessageId != user.MessageId).ToArray();
        var historicalRuns = await _agentRuns!.LoadConversationRunsAsync(scope,
            rawHistory.Where(message => message.Role == ChatMessageRole.User).Select(message => message.MessageId).ToArray(), token);
        preparationLease.CheckCurrent(); CheckCurrent();
        var historicalMessages = NativeConversationHistory.Project(rawHistory, historicalRuns);
        var toolSources = NativeConversationHistory.DescribeTools(historicalRuns);

        CompactionCallBudget SummaryCalls(IModelRequestBudget budget) => new(budget,
            budget is AgentRunState state ? Math.Clamp(state.Snapshot.Budget.MaxModelRequests - state.Snapshot.ModelRequests - 1, 0, 3) : 3);

        async ValueTask<StepEnvironment> CompactStep(StepEnvironment step, IModelRequestBudget budget, CancellationToken cancellation)
        {
            CheckCurrent();
            var model = preparedTurn ?? throw new AgentStateException("RUN_HOST_CLOSED");
            var number = budget is AgentRunState state ? state.Snapshot.StepNumber : 0;
            ConversationContextDiagnostics.Record(_logger, identity.RunId, number, "before_compact", step, model.MeasureInputTokens);
            if (_summaries is null) return step;
            int Measure(ImmutableArray<ModelMessage> messages) => model.MeasureInputTokens(step with { Messages = messages });
            if (Measure(step.Messages) >= CompactionPlanner.TriggerTokens(model.Budget))
                Publish(new(ConversationUpdateType.RequestStage, scope.ConversationId, ServantId: scope.RoleId,
                    RequestStage: ConversationRequestStage.Compacting));
            var compacted = await _summaries.TryCompactNativeAsync(step.Messages, model.Provider, model.Route, model.Budget,
                Measure, scope.RoleId, scope.ConversationId, SummaryCalls(budget), cancellation);
            CheckCurrent(); model.EnsureCurrent(cancellation);
            var result = step with { Messages = compacted };
            ConversationContextDiagnostics.Record(_logger, identity.RunId, number, "after_compact", result, model.MeasureInputTokens);
            return result;
        }
        ValueTask<IAgentModelStep> CreateModel(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var model = preparedTurn;
            model.NativeRequestPreparation = PreparedModel.PrepareRequest;
            return ValueTask.FromResult<IAgentModelStep>(new PreparedModel(model, async (budget, prepareToken) =>
            {
                using var lease = _lifetime.Acquire(prepareToken);
                CheckCurrent();
                lease.CheckCurrent(); CheckCurrent(); model.EnsureCurrent(lease.Token);
                var sources = recalled.Sources.Where(hit => _conversations.IsCurrentSource(semanticScope, hit)).ToArray();
                if (sources.Length != recalled.Sources.Count) recalled = new(RecallStatus.Unavailable, sources);
                var persona = binding.Persona ?? FallbackPersona(binding.Context);
                ImmutableArray<ModelMessage> Project(IReadOnlyList<PromptMessage> prompt, IReadOnlyList<ChatMessage> messages)
                {
                    var history = messages.Where(message => message.MessageId != user.MessageId).ToArray();
                    // Native history is wrapped and measured through its own protocol exactly once.
                    var prefix = prompt.Take(prompt.Count - 1)
                        .Select(message => new ModelMessage(ModelMessageRole.System, message.Text, []));
                    return prefix.Concat(NativeConversationHistory.Project(history, historicalRuns))
                        .Append(new(ModelMessageRole.User, prompt[^1].Text, [])).ToImmutableArray();
                }
                ComposedPrompt Compose(ConversationSummary? summary, IReadOnlyList<ChatMessage> messages)
                {
                    lease.CheckCurrent(); CheckCurrent(); model.EnsureCurrent(lease.Token);
                    var currentSources = sources.Where(hit => _conversations.IsCurrentSource(semanticScope, hit)).ToArray();
                    return _composer.ComposeNative(new PromptContext(binding.Context, persona, binding.Knowledge,
                        [], string.Empty, [], user.Text, context,
                        null, currentSources, currentSources.Length == sources.Length ? recalled.Status : RecallStatus.Unavailable, summary),
                        model.Route, model.Budget, value =>
                        {
                            var projected = Project(value, messages);
                            // These mandatory rules are inserted by the runtime after preparation.
                            // Reserve them while selecting optional background, without duplicating them in history.
                            var measured = projected.Insert(0, new(ModelMessageRole.System, NativeAgentEnvironment.CorePrompt, []));
                            var input = model.MeasureInputTokens(new(measured, _nativeRuntime!.GetPreparationTools(scope)));
                            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { model.Route, Messages = measured })));
                            return new(input, TokenCountKind.Estimated, fingerprint);
                        });
                }
                var snapshot = _contextStore?.Read(semanticScope, scope.ConversationId);
                var prompt = Compose(snapshot?.Summary, snapshot?.UncoveredMessages ?? _conversations.LoadMessages(scope.ConversationId, scope.RoleId));
                if (_summaries is not null && snapshot is not null && prompt.Usage.InputTokens >= CompactionPlanner.TriggerTokens(model.Budget))
                {
                    Publish(new(ConversationUpdateType.RequestStage, scope.ConversationId, ServantId: scope.RoleId,
                        RequestStage: ConversationRequestStage.Compacting));
                    await _summaries.TryCompactAsync(snapshot, model.Provider, model.Route, model.Budget,
                        prompt.Usage.InputTokens, Compose, lease, () => { CheckCurrent(); model.EnsureCurrent(lease.Token); },
                        SummaryCalls(budget), lease.Token, toolSources);
                    snapshot = _contextStore!.Read(semanticScope, scope.ConversationId);
                    prompt = Compose(snapshot.Summary, snapshot.UncoveredMessages);
                }
                var projected = Project(prompt.Messages, snapshot?.UncoveredMessages ?? rawHistory);
                var compacted = await CompactStep(new(projected, []), budget, lease.Token);
                lease.CheckCurrent(); CheckCurrent();
                if (model.MeasureInputTokens(compacted) > model.Budget.InputTokens)
                    throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
                Publish(new(ConversationUpdateType.HistorySources, scope.ConversationId, ServantId: scope.RoleId,
                    HistorySources: sources.Where(hit => prompt.Messages.Any(message => message.Text.Contains(
                        $"recalled:{hit.Anchor.ConversationId}:{hit.Anchor.MessageId}", StringComparison.Ordinal))).ToArray()));
                return compacted.Messages;
            }, historicalMessages, CompactStep, user.Text, CheckCurrent, () => _logger?.LogWarning(
                new EventId(4103, "NativeFinalFormatRepair"), "Native final repair run={RunHash} code={ErrorCode}",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.RunId))), "MODEL_INVALID_FINAL")));
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
            var output = StructuredOutputValidator.ValidateFinalEnvelope(checkpoint.FinalText,
                ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal), user.Text);
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
        var result = await _nativeRuntime!.StartAsync(new(identity,
            new RunBudget(maxModelRequests: 32, maxToolCalls: 64, reserveFinalResponse: true),
            [new(ModelMessageRole.User, user.Text, [])], query),
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
        Func<IModelRequestBudget, CancellationToken, ValueTask<ImmutableArray<ModelMessage>>> prepare,
        ImmutableArray<ModelMessage> history,
        Func<StepEnvironment, IModelRequestBudget, CancellationToken, ValueTask<StepEnvironment>> compact,
        string userText, Action checkCurrent, Action reportFormatRepair)
        : IAgentModelStep, IAgentModelInputBudget, IAgentRunPreparation
    {
        public ImmutableArray<ModelMessage> HistoricalMessages => history;
        public bool RetainsToolHistory => true;
        public string FinalOutputInstructions => StructuredOutputValidator.FinalStepInstructions;
        public int InputTokenBudget => model.InputTokenBudget;
        public int MeasureInputTokens(StepEnvironment environment) => model.MeasureInputTokens(environment);
        public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token)
        {
            var response = await model.ExecuteAsync(environment, budget, token);
            if (model.LastRequestKind == ModelStepKind.Tools)
            {
                if (response.IsFinal) throw new AgentProtocolException("MODEL_EXPECTED_FINALIZE");
                NativeFinalization.Validate(response);
                return response;
            }
            if (!response.IsFinal) throw new AgentProtocolException("MODEL_UNEXPECTED_TOOL_CALL");
            if (ValidFinal(response)) return response;
            checkCurrent();
            reportFormatRepair();
            // A format-only retry cannot execute or replay business calls. The same durable budget,
            // cancellation, connection and input limits apply before another provider request.
            var repair = environment with
            {
                Tools = [],
                Messages = environment.Messages.Add(response.AssistantMessage).Add(new(ModelMessageRole.System,
                    "The final answer failed JSON validation. Correct its format once: return a complete JSON object with text and emotion. " +
                    "Escape quotes and control characters. Preserve the answer, source attribution and uncertainty; do not invent facts or actions. " +
                    "No tools are available; do not request or repeat any tool call.", []))
            };
            var corrected = await model.ExecuteAsync(repair, budget, token);
            checkCurrent();
            if (!corrected.IsFinal || !ValidFinal(corrected)) throw new AgentProtocolException("MODEL_INVALID_FINAL");
            return corrected;
        }
        internal static StepEnvironment PrepareRequest(StepEnvironment environment)
        {
            var kind = environment.Kind == ModelStepKind.FinalOutput || environment.Tools.IsEmpty
                ? ModelStepKind.FinalOutput : ModelStepKind.Tools;
            var messages = ImmutableArray.CreateBuilder<ModelMessage>();
            for (var index = 0; index < environment.Messages.Length; index++)
            {
                var message = environment.Messages[index];
                // The durable control receipt carries no business evidence. Do not spend final
                // context on it; keep every business call/result and the run journal intact.
                if (kind == ModelStepKind.FinalOutput && message.Role == ModelMessageRole.Assistant
                    && message.ToolCalls is [{ IsResolved: true, Name: NativeFinalization.Name } call]
                    && index + 1 < environment.Messages.Length && environment.Messages[index + 1] is
                        { Role: ModelMessageRole.Tool } result && result.ToolCallId == call.CallId)
                { index++; continue; }
                if (message.Role != ModelMessageRole.System || message.Content != StructuredOutputValidator.FinalStepInstructions
                    && message.Content != NativeFinalization.Instructions) messages.Add(message);
            }
            return environment with
            {
                Kind = kind, Tools = kind == ModelStepKind.FinalOutput ? [] : environment.Tools,
                Messages = messages.ToImmutable().Add(new(ModelMessageRole.System, kind == ModelStepKind.FinalOutput
                    ? StructuredOutputValidator.FinalStepInstructions : NativeFinalization.Instructions, []))
            };
        }
        private bool ValidFinal(ModelStepResponse response)
        {
            try
            {
                _ = StructuredOutputValidator.ValidateFinalEnvelope(response.AssistantMessage.Content,
                    ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal), userText);
                return true;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
        }
        public ValueTask<ImmutableArray<ModelMessage>> PrepareAsync(ImmutableArray<ModelMessage> initialMessages,
            IModelRequestBudget budget, CancellationToken token) => prepare(budget, token);
        public ValueTask<StepEnvironment> PrepareStepAsync(StepEnvironment environment, IModelRequestBudget budget,
            CancellationToken token) => compact(environment, budget, token);
    }
}
