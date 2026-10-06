using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;

namespace FgoPet.Agent.Tests.TestDoubles;

/// <summary>Offline fixture. Committing synthetic Command intents does not grant product permission.</summary>
public sealed class ScriptedToolPipeline(params ToolExecutionOutcome[] outcomes) : IToolExecutionPipeline
{
    private readonly Queue<ToolExecutionOutcome> _outcomes = new(outcomes);
    public ToolDescriptor Descriptor { get; init; } = new("fixture.read", "Synthetic read", "{\"type\":\"object\"}", ToolEffect.ReadOnly);
    public List<ToolExecutionRequest> Requests { get; } = [];
    public List<ToolExecutionRequest> Invocations { get; } = [];
    public Func<ToolExecutionRequest, CancellationToken, ValueTask>? BeforeInvokeAsync { get; set; }

    public async ValueTask<ToolExecutionOutcome> AdvanceAsync(ToolExecutionRequest request,
        IToolExecutionIntent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add(request);
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue()
            : new(ToolExecutionOutcomeKind.Completed, new(true, JsonSerializer.SerializeToElement(new { call = request.Call.CallId })));
        if (outcome.Kind is ToolExecutionOutcomeKind.WaitingApproval or ToolExecutionOutcomeKind.WaitingUserInput
            || outcome.Result?.ErrorCode == "TOOL_AUTHORIZATION_DENIED") return outcome;
        await intent.CommitStartedAsync(Descriptor, token);
        token.ThrowIfCancellationRequested();
        Invocations.Add(request);
        if (BeforeInvokeAsync is not null) await BeforeInvokeAsync(request, token);
        return outcome;
    }
}
