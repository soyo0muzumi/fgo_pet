using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

internal static class NativeFinalization
{
    internal const string Name = "response.finalize";
    internal static readonly string Instructions = "Tools only; do not answer the user in this step. " +
        $"When ready to answer, first call {ModelToolNameMap.GetWireName(Name)} alone with {{}} and no text. " +
        "The host will then send a separate final-output request with no tools for your answer.";
    internal static readonly ToolDescriptor Tool = new(Name, "Finish tool selection without text or permission.",
        "{\"type\":\"object\",\"additionalProperties\":false}", ToolEffect.ReadOnly);

    internal static void Validate(ModelStepResponse response)
    {
        if (!response.ToolCalls.Any(call => call.IsResolved && call.Name == Name)) return;
        if (response.ToolCalls.Length != 1 || !string.IsNullOrWhiteSpace(response.AssistantMessage.Content)
            || !response.ToolCalls[0].TryGetArguments(out var arguments) || arguments.EnumerateObject().Any())
            throw new AgentProtocolException("MODEL_INVALID_FINALIZE");
    }

    internal static bool Completed(AgentRunCheckpoint checkpoint) => checkpoint.Calls.Length == 1
        && checkpoint.NextCallIndex == 1 && checkpoint.Calls[0] is { Status: AgentCallStatus.Completed, Result.Success: true } call
        && call.Call.IsResolved && call.Call.Name == Name;

    internal static ToolExecutionOutcome Result() => new(ToolExecutionOutcomeKind.Completed,
        new(true, JsonSerializer.SerializeToElement(new { phase = "final-output" })));
}
