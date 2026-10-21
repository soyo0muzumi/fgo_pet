using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace FgoPet.Kernel.Agent;

internal sealed class AgentLoop(IStepEnvironmentBuilder environment, IToolExecutionPipeline pipeline, IAgentRunFence fence,
    NativeAgentExtensions? extensions = null)
{
    private static readonly JsonSerializerOptions ObservationJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public async ValueTask AdvanceAsync(AgentRunSession session, CancellationToken token)
    {
        var state = session.State;
        while (!state.Snapshot.IsTerminal)
        {
            token.ThrowIfCancellationRequested();
            fence.EnsureCurrent(state.Snapshot.Identity, token);
            if (state.Checkpoint.NextCallIndex < state.Checkpoint.Calls.Length)
            {
                var call = state.CurrentCall().Call;
                await state.ReserveToolAsync(token);
                var load = extensions?.Skills is null ? null : state.Checkpoint.Calls.FirstOrDefault(
                    item => item.Call.IsResolved && item.Call.Name == "skill.load");
                if (load is not null && call.CallId != load.Call.CallId)
                {
                    // The whole batch was chosen before this load's instructions reached the model.
                    // Close sibling calls without execution; the next model step must reconsider them.
                    var deferred = await state.DeferForSkillLoadAsync(token);
                    AppendObservation(session, call.CallId, deferred);
                    if (state.Checkpoint.NextCallIndex == state.Checkpoint.Calls.Length) await state.StepCompletedAsync(token);
                    continue;
                }
                ToolExecutionOutcome outcome;
                if (call.Name == NativeFinalization.Name && call.IsResolved
                    && session.Model is IAgentRunPreparation { FinalOutputInstructions: not null })
                {
                    await state.CommitStartedAsync(NativeFinalization.Tool, token);
                    outcome = NativeFinalization.Result();
                }
                else if (call.Name == "skill.load" && call.IsResolved && extensions?.Skills is not null)
                    outcome = await NativeAgentEnvironment.LoadAsync(extensions.Skills, session, call, token);
                else if (call.Name == "user.ask" && call.IsResolved && extensions?.Questions is not null)
                {
                    if (!call.TryGetArguments(out var arguments)) outcome = ToolResultNormalizer.Failure("TOOL_INVALID_ARGUMENTS");
                    else
                    {
                        try
                        {
                            var question = extensions.Questions.Create(state.Snapshot.Identity, state.Snapshot.StepNumber,
                                call.CallId, state.Snapshot.Revision + 1, arguments);
                            var binding = question.Binding;
                            outcome = new(ToolExecutionOutcomeKind.WaitingUserInput,
                                Waiting: new(binding.RequestId, AgentWaitKind.UserInput, binding.StepNumber, binding.CallId,
                                    binding.WaitingRevision, binding.ExpiresAt)) { UserInputRequest = question };
                        }
                        catch (AgentStateException) { outcome = ToolResultNormalizer.Failure("TOOL_INVALID_ARGUMENTS"); }
                    }
                }
                else outcome = await pipeline.AdvanceAsync(new(state.Snapshot.Identity, state.Snapshot.StepNumber, call)
                    { CheckpointRevision = state.Snapshot.Revision, Approval = state.CurrentCall().Approval }, state, token);
                await state.CompleteToolAsync(outcome, token);
                if (state.Checkpoint.Waiting is not null) return;
                if (outcome.Kind == ToolExecutionOutcomeKind.ExecutionUnknown)
                {
                    await state.FinishAsync(AgentRunStatus.ExecutionUnknown, "TOOL_EXECUTION_UNKNOWN", token);
                    return;
                }
                var result = outcome.Result!;
                AppendObservation(session, call.CallId, result);
                if (state.Checkpoint.NextCallIndex == state.Checkpoint.Calls.Length) await state.StepCompletedAsync(token);
                continue;
            }
            var hasOutputContract = session.Model is IAgentRunPreparation { FinalOutputInstructions: not null };
            var finalized = hasOutputContract && NativeFinalization.Completed(state.Checkpoint);
            await state.BeginStepAsync(token);
            var step = await environment.BuildAsync(state.Snapshot, session.Transcript, token);
            if (extensions is not null) step = await NativeAgentEnvironment.BuildAsync(extensions, session, step, token);
            var finalOnly = state.Snapshot.Budget.ReserveFinalResponse
                && (state.Snapshot.ModelRequests >= state.Snapshot.Budget.MaxModelRequests - 1
                    || state.Snapshot.ToolCalls >= state.Snapshot.Budget.MaxToolCalls);
            if (finalOnly) step = FinalEnvelope(step);
            else if (hasOutputContract && (finalized || step.Tools.IsEmpty))
                step = step with { Kind = ModelStepKind.FinalOutput, Tools = [] };
            if (session.Model is IAgentRunPreparation preparation)
            {
                // Compact the actual final envelope, including fresh memory, host rules and tools.
                // Per-step system blocks must not accumulate in the retained transcript.
                step = await preparation.PrepareStepAsync(step, state, token);
                fence.EnsureCurrent(state.Snapshot.Identity, token);
                // Auxiliary summary calls spend the same budget and can consume the penultimate slot.
                if (!finalOnly && state.Snapshot.Budget.ReserveFinalResponse
                    && (state.Snapshot.ModelRequests >= state.Snapshot.Budget.MaxModelRequests - 1
                        || state.Snapshot.ToolCalls >= state.Snapshot.Budget.MaxToolCalls))
                {
                    finalOnly = true;
                    step = FinalEnvelope(step);
                }
                ModelProtocol.ValidateTranscript(step.Messages);
                StepEnvironmentBuilder.CheckSize(step.Messages);
                if (preparation.RetainsToolHistory && session.Model is IAgentModelInputBudget input
                    && input.MeasureInputTokens(step) > input.InputTokenBudget)
                    throw new AgentProtocolException("RUN_CONTEXT_TOO_LARGE");
                var transientSystem = step.Messages.Where(message => message.Role == ModelMessageRole.System)
                    .Except(session.Transcript).ToHashSet();
                session.SetPreparedTranscript(step.Messages.Where(message => !transientSystem.Contains(message)).ToImmutableArray());
            }
            var before = state.Snapshot.ModelRequests;
            var response = await session.Model!.ExecuteAsync(step, state, token);
            token.ThrowIfCancellationRequested();
            fence.EnsureCurrent(state.Snapshot.Identity, token);
            if (state.Snapshot.ModelRequests <= before) throw new AgentStateException("RUN_UNCHARGED_MODEL");
            if (finalOnly && !response.IsFinal) throw new AgentProtocolException("MODEL_UNEXPECTED_TOOL_CALL");
            if (hasOutputContract) NativeFinalization.Validate(response);
            await state.AcceptResponseAsync(response, token);
            session.Append(response.AssistantMessage);
            if (response.IsFinal)
            {
                await state.CompleteAsync(response.AssistantMessage.Content, token);
                return;
            }
        }
    }

    private static StepEnvironment FinalEnvelope(StepEnvironment step) => step with
    {
        Tools = [], Kind = ModelStepKind.FinalOutput,
        Messages = step.Messages.Insert(0, new(ModelMessageRole.System,
            "The remaining run budget is reserved for a final answer. No tools are available. " +
            "Use only observations already received. State partial coverage and unread ranges when applicable; " +
            "never imply a whole page or task was completed from partial observations. If evidence is insufficient, say so.", []))
    };

    internal static void AppendObservation(AgentRunSession session, string callId, FgoPet.Extensibility.ToolResult result)
    {
        // This call has already been persisted, including denial/defer/resume outcomes.
        // Never infer its recorded Effect from payload, current registry or another call.
        var checkpoint = session.State.Checkpoint;
        var call = checkpoint.Calls.Single(item => item.Call.CallId == callId);
        var identity = checkpoint.Snapshot.Identity;
        var observation = JsonSerializer.Serialize(new
        {
            // ToolCallId and the paired assistant call already identify the call/name in this run.
            metadata = new { sourceKind = "current_execution", runId = identity.RunId,
                toolEffect = call.Effect?.ToString(), callStatus = call.Status.ToString() },
            success = result.Success, payload = result.Payload,
            errorCode = result.ErrorCode, executionState = result.ExecutionState?.ToString()
        }, ObservationJson);
        session.Append(new(ModelMessageRole.Tool, observation, [], callId));
    }
}
