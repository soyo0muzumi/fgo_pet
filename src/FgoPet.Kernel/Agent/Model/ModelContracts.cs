using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Core.Dialogue;

namespace FgoPet.Kernel.Agent;

public enum ModelMessageRole { System, User, Assistant, Tool }

public sealed record ModelToolCall(string CallId, string Name, string ArgumentsJson)
{
    /// <summary>Set false by the adapter when the current request's name map has no match.</summary>
    public bool IsResolved { get; init; } = true;

    public bool TryGetArguments(out JsonElement arguments)
    {
        arguments = default;
        try
        {
            using var document = JsonDocument.Parse(ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            arguments = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }
}

public sealed record ModelMessage(ModelMessageRole Role, string Content,
    ImmutableArray<ModelToolCall> ToolCalls, string? ToolCallId = null);

public sealed record ModelStepResponse(ModelMessage AssistantMessage, string FinishReason,
    bool IsComplete, ChatUsage? Usage = null)
{
    public ImmutableArray<ModelToolCall> ToolCalls => AssistantMessage.ToolCalls;
    public bool IsFinal => ToolCalls.IsEmpty && IsComplete && FinishReason == "stop"
        && !string.IsNullOrWhiteSpace(AssistantMessage.Content);
}

public sealed class AgentProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
