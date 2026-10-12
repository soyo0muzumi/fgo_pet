using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public sealed record StepEnvironment(ImmutableArray<ModelMessage> Messages, ImmutableArray<ToolDescriptor> Tools);

public interface IAgentModelStep
{
    ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment, IModelRequestBudget budget,
        CancellationToken token);
}
/// <summary>Owner preparation may compact semantic history; every provider attempt uses the same durable Run budget.</summary>
public interface IAgentRunPreparation
{
    // Only the trusted host preparation supplies historical evidence, never model output.
    ImmutableArray<ModelMessage> HistoricalMessages => [];
    bool RetainsToolHistory => false;
    ValueTask<ImmutableArray<ModelMessage>> PrepareAsync(ImmutableArray<ModelMessage> initialMessages,
        IModelRequestBudget budget, CancellationToken token);
    ValueTask<StepEnvironment> PrepareStepAsync(StepEnvironment environment, IModelRequestBudget budget,
        CancellationToken token) => ValueTask.FromResult(environment);
}
/// <summary>Exact current provider envelope measurement, including schemas and output reservation.</summary>
public interface IAgentModelInputBudget
{
    int InputTokenBudget { get; }
    int MeasureInputTokens(StepEnvironment environment);
}
public interface IStepEnvironmentBuilder
{
    ValueTask<StepEnvironment> BuildAsync(AgentRunSnapshot run, ImmutableArray<ModelMessage> transcript,
        CancellationToken token);
}
