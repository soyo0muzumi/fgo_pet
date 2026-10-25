using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Lifecycle;

public sealed class CompactionFinalBudgetTests
{
    private static readonly ToolDescriptor Tool = new("fixture.read", "Read fixture.", "{\"type\":\"object\"}", ToolEffect.ReadOnly);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compaction_consuming_penultimate_request_withholds_tools_on_last_model_request(bool attemptsTool)
    {
        var model = new CompactingModel(attemptsTool, 10000);
        var pipeline = new Pipeline();
        await using var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(),
            (_, _) => ValueTask.FromResult<IAgentModelStep>(model), new StepEnvironmentBuilder(_ => [Tool]), pipeline, new Fence());

        var result = await coordinator.StartAsync(Request(), default);

        Assert.NotNull(model.Sent);
        Assert.Empty(model.Sent!.Tools);
        Assert.Contains(model.Sent.Messages, message => message.Role == ModelMessageRole.System
            && message.Content.Contains("reserved for a final answer", StringComparison.Ordinal));
        Assert.Equal(0, pipeline.Calls);
        Assert.Equal(2, result.Snapshot.ModelRequests);
        Assert.Equal(attemptsTool ? AgentRunStatus.Failed : AgentRunStatus.Completed, result.Snapshot.Status);
        if (attemptsTool) Assert.Equal("MODEL_UNEXPECTED_TOOL_CALL", result.Snapshot.ErrorCode);
    }

    [Fact]
    public async Task Final_instruction_added_after_compaction_cannot_bypass_input_budget()
    {
        var model = new CompactingModel(false, 20);
        var pipeline = new Pipeline();
        await using var coordinator = new AgentRunCoordinator(new InMemoryAgentRunStore(),
            (_, _) => ValueTask.FromResult<IAgentModelStep>(model), new StepEnvironmentBuilder(_ => [Tool]), pipeline, new Fence());

        var result = await coordinator.StartAsync(Request(), default);

        Assert.Equal(AgentRunStatus.Failed, result.Snapshot.Status);
        Assert.Equal("RUN_CONTEXT_TOO_LARGE", result.Snapshot.ErrorCode);
        Assert.Null(model.Sent);
        Assert.Equal(1, result.Snapshot.ModelRequests);
        Assert.Equal(0, pipeline.Calls);
    }

    private static AgentRunRequest Request() => new(new("compact-final", "user", new("conversation", "role", null), "model", 1),
        new(maxModelRequests: 2, reserveFinalResponse: true), [new(ModelMessageRole.User, "current", [])]);

    private sealed class CompactingModel(bool attemptsTool, int capacity) : IAgentModelStep, IAgentRunPreparation, IAgentModelInputBudget
    {
        public bool RetainsToolHistory => true;
        public int InputTokenBudget => capacity;
        public StepEnvironment? Sent { get; private set; }
        public int MeasureInputTokens(StepEnvironment environment) => environment.Messages.Sum(message => message.Content.Length);
        public ValueTask<ImmutableArray<ModelMessage>> PrepareAsync(ImmutableArray<ModelMessage> initial,
            IModelRequestBudget budget, CancellationToken token) => ValueTask.FromResult(initial);
        public async ValueTask<StepEnvironment> PrepareStepAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token)
        {
            await budget.ReserveAsync(token);
            return environment;
        }
        public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget, CancellationToken token)
        {
            Sent = environment;
            await budget.ReserveAsync(token);
            return attemptsTool ? new(new(ModelMessageRole.Assistant, "", [new("new-call", Tool.Name, "{}")]), "tool_calls", true)
                : new(new(ModelMessageRole.Assistant, "done", []), "stop", true);
        }
    }

    private sealed class Pipeline : IToolExecutionPipeline
    {
        public int Calls { get; private set; }
        public ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request, IToolExecutionIntent intent, CancellationToken token)
        {
            Calls++;
            return ValueTask.FromResult(new ToolExecutionOutcome(ToolExecutionOutcomeKind.Completed,
                new(true, JsonSerializer.SerializeToElement(new { value = "fixture" }))));
        }
    }
    private sealed class Fence : IAgentRunFence
    {
        public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token) => token.ThrowIfCancellationRequested();
    }
}
