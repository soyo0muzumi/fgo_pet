using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Scenarios;

public sealed class AgentRunCoordinatorScenarios
{
    [Fact]
    public async Task Plain_final_completes_on_last_model_budget_without_tool_calls()
    {
        var host = new Host(new(Final("synthetic final")));
        var result = await host.Coordinator.StartAsync(Request(new(1, 1, 1)), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal("synthetic final", result.FinalText);
        Assert.True(result.StatePersisted);
        Assert.Equal(1, result.Snapshot.ModelRequests);
        Assert.Equal(0, result.Snapshot.ToolCalls);
        Assert.Empty(host.Pipeline.Requests);
        var request = Assert.Single(host.Model.Requests);
        Assert.Equal(ModelMessageRole.User, Assert.Single(request.Messages).Role);
        Assert.Equal("synthetic user request", request.Messages[0].Content);
    }

    [Fact]
    public async Task Three_tool_steps_feed_real_observations_into_each_next_model_request()
    {
        var host = new Host(new(Calls("a"), Calls("b"), Calls("c"), Final("done")));
        var result = await host.Coordinator.StartAsync(Request(new(4, 3, 1)), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(4, result.Snapshot.ModelRequests);
        Assert.Equal(3, result.Snapshot.ToolCalls);
        Assert.Equal(new[] { "a", "b", "c" }, host.Pipeline.Invocations.Select(item => item.Call.CallId));
        Assert.Equal(new[] { 1, 2, 3 }, host.Pipeline.Invocations.Select(item => item.StepNumber));
        Assert.Equal(4, host.Model.Requests.Count);
        for (var step = 1; step <= 3; step++)
        {
            var messages = host.Model.Requests[step].Messages;
            Assert.Equal(1 + step * 2, messages.Length);
            ModelProtocol.ValidateTranscript(messages);
        }
        Assert.Equal(new[] { "a", "b", "c" }, host.Model.Requests[3].Messages
            .Where(message => message.Role == ModelMessageRole.Tool).Select(message => message.ToolCallId));
        var lastObservation = host.Model.Requests[3].Messages.Last();
        using var json = JsonDocument.Parse(lastObservation.Content);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("c", json.RootElement.GetProperty("payload").GetProperty("call").GetString());
    }

    [Fact]
    public async Task Multiple_calls_execute_serially_and_complete_entire_group_before_next_model()
    {
        var host = new Host(new(Calls("a", "b", "c"), Final("done")));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Pipeline.BeforeInvokeAsync = async (request, token) =>
        {
            if (request.Call.CallId == "a") { reached.SetResult(); await release.Task.WaitAsync(token); }
        };
        var running = host.Coordinator.StartAsync(Request(), default).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("a", Assert.Single(host.Pipeline.Invocations).Call.CallId);
        Assert.Single(host.Model.Requests);
        release.SetResult();
        Assert.Equal(AgentRunStatus.Completed, (await running).Snapshot.Status);
        Assert.Equal(new[] { "a", "b", "c" }, host.Pipeline.Invocations.Select(item => item.Call.CallId));
        var messages = host.Model.Requests[1].Messages;
        Assert.Equal(new[] { ModelMessageRole.User, ModelMessageRole.Assistant,
            ModelMessageRole.Tool, ModelMessageRole.Tool, ModelMessageRole.Tool }, messages.Select(message => message.Role));
        Assert.Equal(new[] { "a", "b", "c" }, messages[1].ToolCalls.Select(call => call.CallId));
        Assert.Equal(new[] { "a", "b", "c" }, messages.Skip(2).Select(message => message.ToolCallId));
        ModelProtocol.ValidateTranscript(messages);
    }

    [Fact]
    public async Task Model_budget_exhaustion_prevents_another_model_dispatch()
    {
        var host = new Host(new(Calls("a"), Calls("b"), Final("must not happen")));
        var result = await host.Coordinator.StartAsync(Request(new(2, 3, 1)), default);
        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Snapshot.Status);
        Assert.Equal(2, result.Snapshot.ModelRequests);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        Assert.Equal(2, host.Model.Requests.Count);
        Assert.Equal(2, host.Pipeline.Invocations.Count);
        Assert.Null(result.FinalText);
        Assert.False(string.IsNullOrWhiteSpace(result.StopMessage));
    }

    [Fact]
    public async Task Tool_budget_exhaustion_stops_mid_group_before_unallowed_call()
    {
        var host = new Host(new(Calls("a", "b", "c"), Final("must not happen")));
        var result = await host.Coordinator.StartAsync(Request(new(2, 2, 1)), default);
        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Snapshot.Status);
        Assert.Equal(1, result.Snapshot.ModelRequests);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        Assert.Equal(new[] { "a", "b" }, host.Pipeline.Invocations.Select(item => item.Call.CallId));
        Assert.Single(host.Model.Requests);
        var checkpoint = await host.Store.LoadAsync("run", default);
        Assert.Equal(2, checkpoint!.NextCallIndex);
        Assert.Equal(AgentCallStatus.NotExecuted, checkpoint.Calls[2].Status);
    }

    [Fact]
    public async Task Recoverable_error_observation_is_paired_and_counted_before_alternate_action()
    {
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(ToolExecutionOutcomeKind.Completed,
            new(false, JsonSerializer.SerializeToElement(new { }), "TOOL_EXECUTION_FAILED")));
        var host = new Host(new(Calls("a"), Calls("b"), Final("recovered")), pipeline);
        var result = await host.Coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        Assert.Equal(new[] { "a", "b" }, pipeline.Invocations.Select(item => item.Call.CallId));
        var observation = host.Model.Requests[1].Messages.Last();
        Assert.Equal("a", observation.ToolCallId);
        using var json = JsonDocument.Parse(observation.Content);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("TOOL_EXECUTION_FAILED", json.RootElement.GetProperty("errorCode").GetString());
        ModelProtocol.ValidateTranscript(host.Model.Requests[1].Messages);
    }

    [Theory]
    [InlineData(ToolExecutionOutcomeKind.WaitingApproval, AgentRunStatus.WaitingApproval, AgentWaitKind.Approval)]
    [InlineData(ToolExecutionOutcomeKind.WaitingUserInput, AgentRunStatus.WaitingUserInput, AgentWaitKind.UserInput)]
    public async Task Waiting_mid_group_stops_remaining_calls_and_model_requests(ToolExecutionOutcomeKind kind,
        AgentRunStatus status, AgentWaitKind waitKind)
    {
        var waiting = new AgentWaitState("wait", waitKind, 1, "b", 0, DateTimeOffset.UtcNow.AddMinutes(5));
        var pipeline = new ScriptedToolPipeline(new(ToolExecutionOutcomeKind.Completed,
            new(true, JsonSerializer.SerializeToElement(new { call = "a" }))), new(kind, Waiting: waiting));
        var host = new Host(new(Calls("a", "b", "c"), Final("must not happen")), pipeline);
        var result = await host.Coordinator.StartAsync(Request(), default);
        Assert.Equal(status, result.Snapshot.Status);
        Assert.Equal("wait", result.Waiting!.RequestId);
        Assert.True(result.StatePersisted);
        Assert.Equal(2, result.Snapshot.ToolCalls);
        Assert.Single(host.Model.Requests);
        Assert.Equal("a", Assert.Single(pipeline.Invocations).Call.CallId);
        Assert.Equal(new[] { "a", "b" }, pipeline.Requests.Select(item => item.Call.CallId));
        var checkpoint = await host.Store.LoadAsync("run", default);
        Assert.Equal(1, checkpoint!.NextCallIndex);
        Assert.Equal(AgentCallStatus.Completed, checkpoint.Calls[0].Status);
        Assert.Equal(AgentCallStatus.Requested, checkpoint.Calls[2].Status);
    }

    [Fact]
    public async Task Cancellation_during_read_only_call_prevents_remaining_calls_and_persists_terminal()
    {
        var host = new Host(new(Calls("a", "b"), Final("must not happen")));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Pipeline.BeforeInvokeAsync = async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var running = host.Coordinator.StartAsync(Request(), default).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await host.Coordinator.CancelAsync("run", new("other", "role", null), default));
        Assert.True(await host.Coordinator.CancelAsync("run", Scope, default));
        var result = await running;
        Assert.Equal(AgentRunStatus.Cancelled, result.Snapshot.Status);
        Assert.True(result.StatePersisted);
        Assert.Single(host.Model.Requests);
        Assert.Equal("a", Assert.Single(host.Pipeline.Invocations).Call.CallId);
        Assert.Equal(AgentRunStatus.Cancelled, (await host.Coordinator.GetSnapshotAsync("run", Scope, default))!.Status);
        Assert.Null(await host.Coordinator.GetSnapshotAsync("run", new("other", "role", null), default));
    }

    [Fact]
    public async Task Same_conversation_admission_remains_closed_until_active_run_stops()
    {
        var model = new ScriptedAgentModelStep(Final("unreachable"));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.BeforeResponseAsync = async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var host = new Host(model);
        var running = host.Coordinator.StartAsync(Request(), default).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Request() with { Identity = Request().Identity with { RunId = "second", Scope = Scope with { RoleId = "another-role" } } };
        await Assert.ThrowsAsync<AgentStateException>(() => host.Coordinator.StartAsync(second, default).AsTask());
        Assert.Single(model.Requests);
        Assert.True(await host.Coordinator.CancelAsync("run", Scope, default));
        Assert.Equal(AgentRunStatus.Cancelled, (await running).Snapshot.Status);
        model.BeforeResponseAsync = null;
        Assert.Equal(AgentRunStatus.Completed, (await host.Coordinator.StartAsync(second, default)).Snapshot.Status);
    }

    [Fact]
    public async Task Command_unknown_outcome_halts_without_model_recovery_or_next_call()
    {
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(ToolExecutionOutcomeKind.ExecutionUnknown,
            new(false, JsonSerializer.SerializeToElement(new { }), "TOOL_EXECUTION_UNKNOWN")
            { ExecutionState = ToolExecutionState.Unknown }, ErrorCode: "TOOL_EXECUTION_UNKNOWN"))
        { Descriptor = new("fixture.read", "Synthetic command fixture", "{\"type\":\"object\"}", ToolEffect.Command) };
        var host = new Host(new(Calls("a", "b"), Final("must not happen")), pipeline);
        var result = await host.Coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.ExecutionUnknown, result.Snapshot.Status);
        Assert.Single(host.Model.Requests);
        Assert.Equal("a", Assert.Single(pipeline.Invocations).Call.CallId);
        Assert.Null(result.FinalText);
    }

    [Fact]
    public async Task Denied_call_counts_budget_and_returns_paired_observation_without_invocation()
    {
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(ToolExecutionOutcomeKind.Completed,
            new(false, JsonSerializer.SerializeToElement(new { }), "TOOL_AUTHORIZATION_DENIED")));
        var host = new Host(new(Calls("a"), Final("denied safely")), pipeline);
        var result = await host.Coordinator.StartAsync(Request(new(2, 1, 1)), default);
        Assert.Equal(AgentRunStatus.Completed, result.Snapshot.Status);
        Assert.Equal(1, result.Snapshot.ToolCalls);
        Assert.Empty(pipeline.Invocations);
        Assert.Single(pipeline.Requests);
        var observation = host.Model.Requests[1].Messages.Last();
        Assert.Equal("a", observation.ToolCallId);
        using var json = JsonDocument.Parse(observation.Content);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("TOOL_AUTHORIZATION_DENIED", json.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(AgentWaitKind.Approval)]
    [InlineData(AgentWaitKind.UserInput)]
    public async Task Cancelling_waiting_run_clears_wait_and_releases_conversation_without_more_execution(AgentWaitKind kind)
    {
        var waiting = new AgentWaitState("wait", kind, 1, "a", 0, DateTimeOffset.UtcNow.AddMinutes(5));
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(
            kind == AgentWaitKind.Approval ? ToolExecutionOutcomeKind.WaitingApproval : ToolExecutionOutcomeKind.WaitingUserInput,
            Waiting: waiting));
        var host = new Host(new(Calls("a", "b"), Final("next run")), pipeline);
        await host.Coordinator.StartAsync(Request(), default);
        Assert.True(await host.Coordinator.CancelAsync("run", Scope, default));
        var checkpoint = await host.Store.LoadAsync("run", default);
        Assert.Equal(AgentRunStatus.Cancelled, checkpoint!.Snapshot.Status);
        Assert.Null(checkpoint.Waiting);
        Assert.Empty(pipeline.Invocations);
        Assert.Single(host.Model.Requests);
        var next = Request() with { Identity = Request().Identity with { RunId = "next" } };
        Assert.Equal(AgentRunStatus.Completed, (await host.Coordinator.StartAsync(next, default)).Snapshot.Status);
        Assert.Empty(pipeline.Invocations);
    }

    [Fact]
    public async Task Accepted_cancel_with_precancelled_caller_token_still_persists_terminal_and_releases_admission()
    {
        var waiting = new AgentWaitState("wait", AgentWaitKind.Approval, 1, "a", 0, DateTimeOffset.UtcNow.AddMinutes(5));
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(ToolExecutionOutcomeKind.WaitingApproval, Waiting: waiting));
        var host = new Host(new(Calls("a", "b"), Final("next run")), pipeline);
        await host.Coordinator.StartAsync(Request(), default);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.True(await host.Coordinator.CancelAsync("run", Scope, caller.Token));
        var checkpoint = await host.Store.LoadAsync("run", default);
        Assert.Equal(AgentRunStatus.Cancelled, checkpoint!.Snapshot.Status);
        Assert.Null(checkpoint.Waiting);
        Assert.Empty(pipeline.Invocations);
        var next = Request() with { Identity = Request().Identity with { RunId = "next" } };
        Assert.Equal(AgentRunStatus.Completed, (await host.Coordinator.StartAsync(next, default)).Snapshot.Status);
        Assert.Empty(pipeline.Invocations);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task Invalid_call_group_is_rejected_before_any_tool_charge_or_invocation(string error)
    {
        var response = error == "duplicate" ? Calls("a", "a") : Calls("a", "");
        var host = new Host(new(response, Final("unreachable")));
        var result = await host.Coordinator.StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Failed, result.Snapshot.Status);
        Assert.Equal(0, result.Snapshot.ToolCalls);
        Assert.Single(host.Model.Requests);
        Assert.Empty(host.Pipeline.Requests);
        Assert.Null(result.FinalText);
    }

    [Fact]
    public async Task Cancellation_after_synthetic_command_intent_is_execution_unknown()
    {
        var pipeline = new ScriptedToolPipeline()
        { Descriptor = new("fixture.read", "Synthetic command fault fixture", "{\"type\":\"object\"}", ToolEffect.Command) };
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.BeforeInvokeAsync = async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var host = new Host(new(Calls("a", "b"), Final("unreachable")), pipeline);
        var running = host.Coordinator.StartAsync(Request(), default).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await host.Coordinator.CancelAsync("run", Scope, default));
        var result = await running;
        Assert.Equal(AgentRunStatus.ExecutionUnknown, result.Snapshot.Status);
        Assert.Equal("TOOL_EXECUTION_UNKNOWN", result.Snapshot.ErrorCode);
        Assert.True(result.StatePersisted);
        Assert.Single(host.Model.Requests);
        Assert.Equal("a", Assert.Single(pipeline.Invocations).Call.CallId);
    }

    [Theory]
    [InlineData(2, AgentRunStatus.Completed, 2)]
    [InlineData(1, AgentRunStatus.BudgetExceeded, 1)]
    public async Task Each_model_retry_attempt_reserves_budget_before_dispatch(int allowed,
        AgentRunStatus status, int expectedDispatched)
    {
        var model = new ScriptedAgentModelStep(Final("final after synthetic retry")) { AttemptsPerStep = 2 };
        var host = new Host(model);
        var result = await host.Coordinator.StartAsync(Request(new(allowed, 1, 1)), default);
        Assert.Equal(status, result.Snapshot.Status);
        Assert.Equal(expectedDispatched, result.Snapshot.ModelRequests);
        Assert.Equal(expectedDispatched, model.Requests.Count);
        Assert.Empty(host.Pipeline.Requests);
    }

    [Theory]
    [InlineData(AgentEventKind.ToolStarted, AgentRunStatus.Failed, 0)]
    [InlineData(AgentEventKind.ToolCompleted, AgentRunStatus.ExecutionUnknown, 1)]
    public async Task Command_storage_fault_prevents_uncommitted_invoke_and_fences_post_effect_replay(
        AgentEventKind failure, AgentRunStatus status, int expectedInvocations)
    {
        var inner = new InMemoryAgentRunStore();
        var store = new ScriptedFaultingRunStore(inner, failure);
        var model = new ScriptedAgentModelStep(Calls("a", "b"), Final("unreachable"));
        var pipeline = new ScriptedToolPipeline(new ToolExecutionOutcome(ToolExecutionOutcomeKind.Completed,
            new(true, JsonSerializer.SerializeToElement(new { committed = true })) { ExecutionState = ToolExecutionState.Committed }))
        { Descriptor = new("fixture.read", "Synthetic command storage fault", "{\"type\":\"object\"}", ToolEffect.Command) };
        var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new ScriptedStepEnvironmentBuilder(pipeline.Descriptor), pipeline, new ScriptedRunFence());
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(status, result.Snapshot.Status);
        Assert.False(result.StatePersisted);
        Assert.Null(result.FinalText);
        Assert.Equal(expectedInvocations, pipeline.Invocations.Count);
        Assert.Single(model.Requests);
        Assert.Equal(status, (await coordinator.GetSnapshotAsync("run", Scope, default))!.Status);
        var checkpoint = await inner.LoadAsync("run", default);
        Assert.Equal(0, checkpoint!.NextCallIndex);
        Assert.Equal(failure == AgentEventKind.ToolStarted ? AgentCallStatus.Requested : AgentCallStatus.Started,
            checkpoint.Calls[0].Status);
        Assert.Equal(AgentCallStatus.Requested, checkpoint.Calls[1].Status);
    }

    private static readonly ToolScope Scope = new("conversation", "role", null);
    private static AgentRunRequest Request(RunBudget? budget = null)
        => new(new("run", "user", Scope, "model", 1), budget ?? new(),
            [new(ModelMessageRole.User, "synthetic user request", [])]);
    private static ModelStepResponse Final(string text)
        => new(new(ModelMessageRole.Assistant, text, []), "stop", true);
    private static ModelStepResponse Calls(params string[] ids)
        => new(new(ModelMessageRole.Assistant, "", [.. ids.Select(id => new ModelToolCall(id, "fixture.read", "{}"))]), "tool_calls", true);
    private sealed class Host
    {
        public InMemoryAgentRunStore Store { get; } = new();
        public ScriptedAgentModelStep Model { get; }
        public ScriptedToolPipeline Pipeline { get; }
        public AgentRunCoordinator Coordinator { get; }
        public Host(ScriptedAgentModelStep model, ScriptedToolPipeline? pipeline = null)
        {
            Model = model;
            Pipeline = pipeline ?? new();
            Coordinator = new(Store, (_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IAgentModelStep>(Model); },
                new ScriptedStepEnvironmentBuilder(Pipeline.Descriptor), Pipeline, new ScriptedRunFence());
        }
    }
}
