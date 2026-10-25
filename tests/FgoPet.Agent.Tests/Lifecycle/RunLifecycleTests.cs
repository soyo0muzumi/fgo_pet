using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Lifecycle;

public sealed class RunLifecycleTests
{
    [Theory]
    [InlineData(6, ToolEffect.ReadOnly, AgentRunStatus.Failed, 0)]
    [InlineData(7, ToolEffect.Command, AgentRunStatus.ExecutionUnknown, 1)]
    public async Task Storage_failure_never_replays_effect_or_advances_queue(int failAt, ToolEffect effect,
        AgentRunStatus expected, int expectedCalls)
    {
        var inner = new InMemoryAgentRunStore();
        var store = new FaultInjectingAgentRunStore(inner) { FailOnCommit = failAt };
        var model = new FixtureModel();
        var pipeline = new FixturePipeline(effect);
        var coordinator = Create(store, model, pipeline, new Fence());
        var result = await coordinator.StartAsync(Request(), default);
        Assert.Equal(expected, result.Snapshot.Status);
        Assert.False(result.StatePersisted);
        Assert.Equal(expectedCalls, pipeline.Invocations);
        Assert.Equal(1, model.Requests);
        Assert.Equal(0, (await inner.LoadAsync("run", default))!.NextCallIndex);
        Assert.Equal(expected, (await coordinator.GetSnapshotAsync("run", Scope, default))!.Status);
        Assert.Null(await coordinator.GetSnapshotAsync("run", Scope with { RoleId = "other" }, default));
    }

    [Fact]
    public async Task Invalidated_generation_after_model_prevents_tools()
    {
        var fence = new Fence();
        var model = new FixtureModel { AfterResponse = () => fence.Current = false };
        var pipeline = new FixturePipeline(ToolEffect.ReadOnly);
        var result = await Create(new InMemoryAgentRunStore(), model, pipeline, fence).StartAsync(Request(), default);
        Assert.Equal(AgentRunStatus.Failed, result.Snapshot.Status);
        Assert.Equal("RUN_SCOPE_CHANGED", result.Snapshot.ErrorCode);
        Assert.Equal(0, pipeline.Invocations);
    }

    [Fact]
    public async Task Cancellation_after_command_intent_preserves_unknown()
    {
        using var cancellation = new CancellationTokenSource();
        var pipeline = new FixturePipeline(ToolEffect.Command) { AfterInvoke = cancellation.Cancel };
        var result = await Create(new InMemoryAgentRunStore(), new FixtureModel(), pipeline, new Fence())
            .StartAsync(Request(), cancellation.Token);
        Assert.Equal(AgentRunStatus.ExecutionUnknown, result.Snapshot.Status);
        Assert.Equal(1, pipeline.Invocations);
        Assert.True(result.StatePersisted);
    }

    [Fact]
    public async Task Precancelled_start_releases_admission()
    {
        var store = new InMemoryAgentRunStore();
        var coordinator = Create(store, new FixtureModel(), new FixturePipeline(ToolEffect.ReadOnly), new Fence());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync(Request(), cancellation.Token).AsTask());
        var next = await coordinator.StartAsync(Request() with { Identity = Request().Identity with { RunId = "next" } }, default);
        Assert.Equal(AgentRunStatus.Completed, next.Snapshot.Status);
    }

    [Fact]
    public async Task Failure_journal_contains_safe_metadata_and_call_error()
    {
        var store = new InMemoryAgentRunStore();
        var state = await RunStateCreate(store);
        await state.StartAsync(default);
        await state.BeginStepAsync(default);
        await state.AcceptResponseAsync(new(new(ModelMessageRole.Assistant, "private-fixture-text",
            [new("call-a", "fixture.read", "{\"secret\":\"private-fixture-text\"}")]), "tool_calls", true), default);
        await state.ReserveToolAsync(default);
        await state.CompleteToolAsync(new(ToolExecutionOutcomeKind.Completed,
            new(false, JsonSerializer.SerializeToElement(new { }), "TOOL_ARGUMENTS_INVALID")), default);
        var journal = store.Events("run");
        Assert.Equal("TOOL_ARGUMENTS_INVALID", journal.Last().ErrorCode);
        Assert.Null(state.Snapshot.ErrorCode);
        var serialized = JsonSerializer.Serialize(journal);
        Assert.DoesNotContain("private-fixture-text", serialized);
        Assert.DoesNotContain("fixture.read", serialized);
        Assert.DoesNotContain("call-a", serialized);
        Assert.DoesNotContain("conversation", serialized);
    }

    private static readonly ToolScope Scope = new("conversation", "role", null);
    private static AgentRunRequest Request() => new(new("run", "user", Scope, "route", 1), new(),
        [new(ModelMessageRole.User, "fixture", [])]);
    private static ValueTask<AgentRunState> RunStateCreate(IAgentRunStore store) =>
        AgentRunState.CreateAsync(Request(), store, TimeProvider.System, default);
    private static AgentRunCoordinator Create(IAgentRunStore store, FixtureModel model, FixturePipeline pipeline, Fence fence) =>
        new(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model), new StepEnvironmentBuilder(_ => []), pipeline, fence);

    private sealed class Fence : IAgentRunFence
    {
        public bool Current { get; set; } = true;
        public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Current) throw new AgentStateException("RUN_SCOPE_CHANGED");
        }
    }
    private sealed class FixtureModel : IAgentModelStep
    {
        public int Requests { get; private set; }
        public Action? AfterResponse { get; init; }
        public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget,
            CancellationToken token)
        {
            await budget.ReserveAsync(token);
            Requests++;
            AfterResponse?.Invoke();
            return Requests == 1 ? new(new(ModelMessageRole.Assistant, "", [new("a", "fixture.read", "{}"),
                new("b", "fixture.read", "{}")]), "tool_calls", true)
                : new(new(ModelMessageRole.Assistant, "final", []), "stop", true);
        }
    }
    private sealed class FixturePipeline(ToolEffect effect) : IToolExecutionPipeline
    {
        public int Invocations { get; private set; }
        public Action? AfterInvoke { get; init; }
        public async ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request, IToolExecutionIntent intent,
            CancellationToken token)
        {
            // Synthetic effects only: no real command or product authorization is granted by this fixture.
            await intent.CommitStartedAsync(new("fixture.read", "fixture", "{}", effect), token);
            Invocations++;
            AfterInvoke?.Invoke();
            token.ThrowIfCancellationRequested();
            return new(ToolExecutionOutcomeKind.Completed, new(true, JsonSerializer.SerializeToElement(new { value = 1 }))
                { ExecutionState = effect == ToolEffect.Command ? ToolExecutionState.Committed : null });
        }
    }
}
