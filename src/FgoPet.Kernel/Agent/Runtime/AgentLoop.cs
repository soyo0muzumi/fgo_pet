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
                ToolExecutionOutcome outcome;
                if (call.Name == "skill.load" && call.IsResolved && extensions?.Skills is not null)
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
            await state.BeginStepAsync(token);
            var step = await environment.BuildAsync(state.Snapshot, session.Transcript, token);
            if (extensions is not null) step = await NativeAgentEnvironment.BuildAsync(extensions, session, step, token);
            var before = state.Snapshot.ModelRequests;
            var response = await session.Model!.ExecuteAsync(step, state, token);
            token.ThrowIfCancellationRequested();
            fence.EnsureCurrent(state.Snapshot.Identity, token);
            if (state.Snapshot.ModelRequests <= before) throw new AgentStateException("RUN_UNCHARGED_MODEL");
            await state.AcceptResponseAsync(response, token);
            session.Append(response.AssistantMessage);
            if (response.IsFinal)
            {
                await state.CompleteAsync(response.AssistantMessage.Content, token);
                return;
            }
        }
    }

    internal static void AppendObservation(AgentRunSession session, string callId, FgoPet.Extensibility.ToolResult result)
    {
        var observation = JsonSerializer.Serialize(new { success = result.Success, payload = result.Payload,
            errorCode = result.ErrorCode, executionState = result.ExecutionState?.ToString() }, ObservationJson);
        session.Append(new(ModelMessageRole.Tool, observation, [], callId));
    }
}
