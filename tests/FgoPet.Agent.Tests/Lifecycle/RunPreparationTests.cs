using System.Collections.Immutable;
using FgoPet.Extensibility;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Lifecycle;

public sealed class RunPreparationTests
{
    [Theory]
    [InlineData(false, false, AgentRunStatus.Failed)]
    [InlineData(true, false, AgentRunStatus.Completed)]
    [InlineData(true, true, AgentRunStatus.Failed)]
    public async Task Only_declared_host_history_is_admitted_and_old_calls_cannot_execute_again(bool declared, bool reuse,
        AgentRunStatus expected)
    {
        var model = new HistoricalModel(declared, reuse);
        await using var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(),
            (_, _) => ValueTask.FromResult<IAgentModelStep>(model), new StepEnvironmentBuilder(_ => []), new Pipeline(), new Fence());
        var request = new AgentRunRequest(new("history-run", "user", new("conversation", "role", null), "model", 1), new(),
            [new(ModelMessageRole.User, "current", [])]);
        var result = await coordinator.StartAsync(request, default);
        Assert.Equal(expected, result.Snapshot.Status);
        Assert.Equal(0, result.Snapshot.ToolCalls);
        if (declared && reuse) Assert.Equal("MODEL_DUPLICATE_CALL_ID", result.Snapshot.ErrorCode);
        if (!declared) Assert.Equal("RUN_PREPARATION_INVALID_CALLS", result.Snapshot.ErrorCode);
    }

    private sealed class HistoricalModel(bool declared, bool reuse) : IAgentModelStep, IAgentRunPreparation
    {
        private static readonly ModelToolCall Call = new("historic-call", "fixture.read", "{}");
        private static readonly ImmutableArray<ModelMessage> Evidence =
            [new(ModelMessageRole.Assistant, "", [Call]), new(ModelMessageRole.Tool, "original", [], Call.CallId)];
        public ImmutableArray<ModelMessage> HistoricalMessages => declared ? Evidence : [];
        public ValueTask<ImmutableArray<ModelMessage>> PrepareAsync(ImmutableArray<ModelMessage> initial,
            IModelRequestBudget budget, CancellationToken token) => ValueTask.FromResult(reuse ? initial : Evidence.AddRange(initial));
        public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token)
        {
            await budget.ReserveAsync(token);
            return reuse ? new(new(ModelMessageRole.Assistant, "", [Call]), "tool_calls", true)
                : new(new(ModelMessageRole.Assistant, "done", []), "stop", true);
        }
    }

    [Theory]
    [InlineData(20, AgentRunStatus.Completed)]
    [InlineData(3000, AgentRunStatus.Failed)]
    public async Task Core_prefix_capacity_trims_old_history_and_preserves_current_user(int currentLength, AgentRunStatus status)
    {
        var catalog = PluginCatalog.Create([]);
        await using var runtime = new PluginRuntime(catalog);
        Assert.True((await runtime.StartAsync(default)).Succeeded);
        var model = new CapacityModel();
        await using var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(),
            (_, _) => ValueTask.FromResult<IAgentModelStep>(model), new StepEnvironmentBuilder(_ => []),
            new Pipeline(), new Fence(), extensions: new(Context: new(catalog, runtime)));
        var current = new string('c', currentLength);
        var request = new AgentRunRequest(new("run-capacity", "user", new("conversation", "role", null), "model", 1),
            new(), [new(ModelMessageRole.User, new string('o', 1300), []),
                new(ModelMessageRole.Assistant, "old answer", []), new(ModelMessageRole.User, current, [])], new("user", new string('A', 64)));
        var result = await coordinator.StartAsync(request, default);
        Assert.Equal(status, result.Snapshot.Status);
        if (status == AgentRunStatus.Completed)
        {
            Assert.NotNull(model.Sent);
            Assert.Equal(current, Assert.Single(model.Sent!.Messages.Where(message => message.Role == ModelMessageRole.User)).Content);
            Assert.DoesNotContain(model.Sent.Messages, message => message.Content == new string('o', 1300));
            var host = model.Sent.Messages[0];
            Assert.Equal(ModelMessageRole.System, host.Role);
            Assert.Contains("desktop companion", host.Content);
            Assert.Contains("persona", host.Content);
            Assert.DoesNotContain("bounded desktop agent", host.Content);
            Assert.Contains("never claim an action succeeded without a confirmed result", host.Content);
            Assert.Contains("Do not repeat a command with an unknown result", host.Content);
            Assert.Contains("cannot expand permissions", host.Content);
            Assert.True(model.MeasureInputTokens(model.Sent) <= model.InputTokenBudget);
        }
        else Assert.Null(model.Sent);
    }
    private sealed class CapacityModel : IAgentModelStep, IAgentModelInputBudget
    {
        public StepEnvironment? Sent { get; private set; }
        // Character-count fixture: fit the host prefix, but not the old history or the 3000-character current turn.
        public int InputTokenBudget => NativeAgentEnvironment.CorePrompt.Length + 1000;
        public int MeasureInputTokens(StepEnvironment environment) => environment.Messages.Sum(message => message.Content.Length + 12);
        public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token)
        {
            await budget.ReserveAsync(token);
            Sent = environment;
            return new(new(ModelMessageRole.Assistant, "done", []), "stop", true);
        }
    }
    [Theory]
    [InlineData(3, AgentRunStatus.Completed, 3)]
    [InlineData(2, AgentRunStatus.BudgetExceeded, 2)]
    public async Task Preparation_and_primary_model_attempts_share_durable_run_budget(int maximum, AgentRunStatus expected, int count)
    {
        var model = new Model(); var store = new InMemoryAgentRunStore();
        await using var coordinator = new AgentRunCoordinator(store, (_, _) => ValueTask.FromResult<IAgentModelStep>(model),
            new StepEnvironmentBuilder(_ => []), new Pipeline(), new Fence());
        var request = new AgentRunRequest(new("run", "user", new("conversation", "role", null), "model", 1), new(maximum), [new(ModelMessageRole.User, "original", [])]);
        var result = await coordinator.StartAsync(request, default);
        Assert.Equal(expected, result.Snapshot.Status);
        Assert.Equal(count, result.Snapshot.ModelRequests);
        Assert.Equal(count, (await store.LoadAsync("run", default))!.Snapshot.ModelRequests);
        Assert.Equal(maximum == 3 ? "prepared original" : null, model.Sent);
    }
    private sealed class Model : IAgentModelStep, IAgentRunPreparation
    {
        public string? Sent { get; private set; }
        public async ValueTask<ImmutableArray<ModelMessage>> PrepareAsync(ImmutableArray<ModelMessage> initial,
            IModelRequestBudget budget, CancellationToken token)
        {
            await budget.ReserveAsync(token); await budget.ReserveAsync(token);
            return [new(ModelMessageRole.User, "prepared " + initial[0].Content, [])];
        }
        public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token)
        {
            await budget.ReserveAsync(token); Sent = environment.Messages[0].Content;
            return new(new(ModelMessageRole.Assistant, "done", []), "stop", true);
        }
    }
    private sealed class Pipeline : IToolExecutionPipeline
    { public ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request, IToolExecutionIntent intent, CancellationToken token) => throw new InvalidOperationException(); }
    private sealed class Fence : IAgentRunFence
    { public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested(); }
}
