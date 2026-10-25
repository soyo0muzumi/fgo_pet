using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;

namespace FgoPet.Agent.Tests.TestDoubles;

public sealed class ScriptedAgentModelStep(params ModelStepResponse[] responses) : IAgentModelStep
{
    private readonly Queue<ModelStepResponse> _responses = new(responses);
    public List<StepEnvironment> Requests { get; } = [];
    public int AttemptsPerStep { get; init; } = 1;
    public Func<StepEnvironment, CancellationToken, ValueTask>? BeforeResponseAsync { get; set; }

    public async ValueTask<ModelStepResponse> ExecuteAsync(StepEnvironment environment,
        IModelRequestBudget budget, CancellationToken token)
    {
        for (var attempt = 0; attempt < AttemptsPerStep; attempt++)
        {
            await budget.ReserveAsync(token);
            Requests.Add(environment);
            if (BeforeResponseAsync is not null) await BeforeResponseAsync(environment, token);
        }
        token.ThrowIfCancellationRequested();
        if (_responses.Count == 0) throw new InvalidOperationException("SCRIPT_EXHAUSTED");
        return _responses.Dequeue();
    }
}

public sealed class ScriptedStepEnvironmentBuilder(params ToolDescriptor[] tools) : IStepEnvironmentBuilder
{
    public ValueTask<StepEnvironment> BuildAsync(AgentRunSnapshot run,
        System.Collections.Immutable.ImmutableArray<ModelMessage> transcript, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new StepEnvironment(transcript, [.. tools]));
    }
}

public sealed class ScriptedRunFence : IAgentRunFence
{
    public bool IsCurrent { get; set; } = true;
    public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent) throw new AgentStateException("RUN_FENCED");
    }
}
