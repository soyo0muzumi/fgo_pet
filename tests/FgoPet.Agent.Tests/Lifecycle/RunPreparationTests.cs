using System.Collections.Immutable;
using FgoPet.Extensibility;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Lifecycle;

public sealed class RunPreparationTests
{
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
            Assert.Contains(model.Sent.Messages, message => message.Role == ModelMessageRole.System && message.Content.Contains("bounded desktop agent", StringComparison.Ordinal));
            Assert.True(model.MeasureInputTokens(model.Sent) <= model.InputTokenBudget);
        }
        else Assert.Null(model.Sent);
    }
    private sealed class CapacityModel : IAgentModelStep, IAgentModelInputBudget
    {
        public StepEnvironment? Sent { get; private set; }
        public int InputTokenBudget => 2000;
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
