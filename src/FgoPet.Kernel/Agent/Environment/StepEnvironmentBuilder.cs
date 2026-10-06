using System.Collections.Immutable;
using System.Text;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Builds a fresh capability view for each model step. Business context belongs to contributions.</summary>
public sealed class StepEnvironmentBuilder(Func<ToolScope, ImmutableArray<ToolDescriptor>> tools) : IStepEnvironmentBuilder
{
    public ValueTask<StepEnvironment> BuildAsync(AgentRunSnapshot run, ImmutableArray<ModelMessage> transcript,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ModelProtocol.ValidateTranscript(transcript);
        CheckSize(transcript);
        return ValueTask.FromResult(new StepEnvironment(transcript, tools(run.Identity.Scope)));
    }

    internal static void CheckSize(ImmutableArray<ModelMessage> messages)
    {
        long size = 0;
        foreach (var message in messages)
        {
            size += Encoding.UTF8.GetByteCount(message.Content) + (message.ToolCallId?.Length ?? 0);
            foreach (var call in message.ToolCalls)
                size += Encoding.UTF8.GetByteCount(call.ArgumentsJson) + call.Name.Length + call.CallId.Length;
            if (size > 1024 * 1024) throw new AgentProtocolException("RUN_TRANSCRIPT_TOO_LARGE");
        }
    }
}
