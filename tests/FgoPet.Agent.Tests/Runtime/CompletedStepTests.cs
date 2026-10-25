using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Agent.Tests.TestDoubles;
using Xunit;

namespace FgoPet.Agent.Tests.Runtime;

public sealed class CompletedStepTests
{
    [Fact]
    public async Task Completed_calls_and_approval_are_archived_across_later_steps_and_final_completion()
    {
        var store = new InMemoryAgentRunStore();
        var state = await CreateAsync(store);
        await state.StartAsync(default);
        await state.BeginStepAsync(default);

        await AcceptCallsAsync(state,
            new ModelToolCall("read-call", "fixture.read", "{\"lookup\":\"alpha\"}"),
            new ModelToolCall("write-call", "fixture.write", "{\"value\":\"first-write\"}"));
        await CompleteCurrentToolAsync(state, "fixture.read", "{\"value\":\"alpha-result\"}");

        await state.ReserveToolAsync(default);
        var approval = CreateApproval(state, "write-call", "{\"value\":\"first-write\"}");
        var waiting = new AgentWaitState(approval.Binding.RequestId, AgentWaitKind.Approval,
            state.Snapshot.StepNumber, "write-call", approval.Binding.WaitingRevision, approval.Binding.ExpiresAt);
        await state.CompleteToolAsync(new ToolExecutionOutcome(ToolExecutionOutcomeKind.WaitingApproval, Waiting: waiting)
        {
            ApprovalRequest = approval
        }, default);
        await state.ResolveApprovalAsync(new("run", approval.Binding.RequestId,
            approval.Binding.WaitingRevision, ApprovalDecision.Allow), default);
        await state.CommitStartedAsync(Descriptor("fixture.write", ToolEffect.Command), default);
        await CompleteReservedToolAsync(state, "{\"accepted\":\"first-write\"}");
        await state.StepCompletedAsync(default);

        await state.BeginStepAsync(default);
        var firstStep = Assert.Single(state.Checkpoint.CompletedSteps);
        Assert.Equal(1, firstStep.StepNumber);
        Assert.Equal(2, firstStep.Calls.Length);
        Assert.Equal("{\"lookup\":\"alpha\"}", firstStep.Calls[0].Call.ArgumentsJson);
        Assert.Equal("alpha-result", firstStep.Calls[0].Result!.Payload.GetProperty("value").GetString());
        Assert.Equal("{\"value\":\"first-write\"}", firstStep.Calls[1].Call.ArgumentsJson);
        Assert.Equal("first-write", firstStep.Calls[1].Result!.Payload.GetProperty("accepted").GetString());
        Assert.Equal(AgentCallStatus.Completed, firstStep.Calls[1].Status);
        Assert.Equal(approval, firstStep.Calls[1].Approval);

        await AcceptCallsAsync(state, new ModelToolCall("next-read-call", "fixture.read", "{\"lookup\":\"beta\"}"));
        await CompleteCurrentToolAsync(state, "fixture.read", "{\"value\":\"beta-result\"}");
        await state.StepCompletedAsync(default);
        await state.BeginStepAsync(default);
        await state.CompleteAsync("Synthetic final answer", default);

        var completed = state.Checkpoint;
        Assert.Equal(AgentRunStatus.Completed, completed.Snapshot.Status);
        Assert.Equal("Synthetic final answer", completed.FinalText);
        Assert.Equal(2, completed.CompletedSteps.Length);
        Assert.Equal(approval, completed.CompletedSteps[0].Calls[1].Approval);
        Assert.Equal(2, completed.CompletedSteps[1].StepNumber);
        Assert.Equal("{\"lookup\":\"beta\"}", completed.CompletedSteps[1].Calls[0].Call.ArgumentsJson);
        Assert.Equal("beta-result", completed.CompletedSteps[1].Calls[0].Result!.Payload.GetProperty("value").GetString());

        var persisted = (await store.LoadAsync("run", default))!;
        Assert.Equal(2, persisted.CompletedSteps.Length);
        Assert.Equal(approval, persisted.CompletedSteps[0].Calls[1].Approval);
        Assert.Equal("beta-result", persisted.CompletedSteps[1].Calls[0].Result!.Payload.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Empty_step_does_not_add_or_duplicate_a_completed_step()
    {
        var store = new InMemoryAgentRunStore();
        var state = await CreateAsync(store);
        await state.StartAsync(default);
        await state.BeginStepAsync(default);
        await AcceptCallsAsync(state, new ModelToolCall("only-call", "fixture.read", "{\"lookup\":\"only\"}"));
        await CompleteCurrentToolAsync(state, "fixture.read", "{\"value\":\"only-result\"}");
        await state.BeginStepAsync(default);

        var archived = Assert.Single(state.Checkpoint.CompletedSteps);
        Assert.Equal(1, archived.StepNumber);
        Assert.Equal("only-result", archived.Calls[0].Result!.Payload.GetProperty("value").GetString());

        await state.BeginStepAsync(default);

        archived = Assert.Single(state.Checkpoint.CompletedSteps);
        Assert.Equal(1, archived.StepNumber);
        Assert.Equal("only-result", archived.Calls[0].Result!.Payload.GetProperty("value").GetString());
        Assert.Single((await store.LoadAsync("run", default))!.CompletedSteps);
    }

    [Fact]
    public async Task Pending_call_queue_prevents_archiving_or_advancing_the_step()
    {
        var store = new InMemoryAgentRunStore();
        var state = await CreateAsync(store);
        await state.StartAsync(default);
        await state.BeginStepAsync(default);
        await AcceptCallsAsync(state,
            new ModelToolCall("first-call", "fixture.read", "{\"lookup\":\"first\"}"),
            new ModelToolCall("pending-call", "fixture.read", "{\"lookup\":\"pending\"}"));
        await CompleteCurrentToolAsync(state, "fixture.read", "{\"value\":\"first-result\"}");

        var error = await Assert.ThrowsAsync<AgentStateException>(() => state.BeginStepAsync(default).AsTask());

        Assert.Equal("RUN_PENDING_CALLS", error.Code);
        Assert.Empty(state.Checkpoint.CompletedSteps);
        Assert.Equal(1, state.Snapshot.StepNumber);
        Assert.Equal(1, state.Checkpoint.NextCallIndex);
        Assert.Equal(2, state.Checkpoint.Calls.Length);
        Assert.Equal("first-result", state.Checkpoint.Calls[0].Result!.Payload.GetProperty("value").GetString());
        Assert.Equal("{\"lookup\":\"pending\"}", state.Checkpoint.Calls[1].Call.ArgumentsJson);
        Assert.Empty((await store.LoadAsync("run", default))!.CompletedSteps);
    }

    private static ValueTask<AgentRunState> CreateAsync(IAgentRunStore store) =>
        AgentRunState.CreateAsync(new(new("run", "user", new("conversation", "role", null), "route", 1), new(),
            [new(ModelMessageRole.User, "Synthetic test request", [])]), store, TimeProvider.System, default);

    private static Task AcceptCallsAsync(AgentRunState state, params ModelToolCall[] calls) =>
        state.AcceptResponseAsync(new(new(ModelMessageRole.Assistant, "", calls.ToImmutableArray()), "tool_calls", true), default).AsTask();

    private static async Task CompleteCurrentToolAsync(AgentRunState state, string name, string resultJson)
    {
        await state.ReserveToolAsync(default);
        await state.CommitStartedAsync(Descriptor(name, ToolEffect.ReadOnly), default);
        await CompleteReservedToolAsync(state, resultJson);
    }

    private static Task CompleteReservedToolAsync(AgentRunState state, string resultJson) =>
        state.CompleteToolAsync(new(ToolExecutionOutcomeKind.Completed, Result(resultJson)), default).AsTask();

    private static ToolDescriptor Descriptor(string name, ToolEffect effect) =>
        new(name, "Synthetic test tool", "{}", effect);

    private static ToolResult Result(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(true, document.RootElement.Clone());
    }

    private static ApprovalRequest CreateApproval(AgentRunState state, string callId, string argumentsJson)
    {
        var binding = new InteractionBinding(state.Snapshot.Identity, state.Snapshot.StepNumber, callId,
            "synthetic-approval-request", state.Snapshot.Revision + 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var tool = new FrozenToolBinding("fixture.plugin", "1.0.0", "fixture.write",
            new string('A', 64), new string('B', 64), "synthetic-authorization", state.Snapshot.Identity.AuthorizationRevision);
        return new(binding, tool, argumentsJson);
    }
}
