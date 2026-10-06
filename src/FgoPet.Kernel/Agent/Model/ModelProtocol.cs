using System.Collections.Immutable;
using System.Text;

namespace FgoPet.Kernel.Agent;

/// <summary>Checks a complete call batch before any effect, and complete request groups before dispatch.</summary>
public static class ModelProtocol
{
    public const int MaxCalls = 16;
    public const int MaxArgumentsBytes = 64 * 1024;
    public const int MaxBatchArgumentsBytes = 256 * 1024;
    public const int MaxContentBytes = 128 * 1024;

    public static void ValidateResponse(ModelStepResponse response, IReadOnlySet<string> previousCallIds)
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateMessage(response.AssistantMessage);
        if (response.AssistantMessage.Role != ModelMessageRole.Assistant || !response.IsComplete)
            throw new AgentProtocolException("MODEL_INCOMPLETE_RESPONSE");
        if (response.ToolCalls.IsEmpty)
        {
            if (!response.IsFinal || response.AssistantMessage.Content.Length > 12000)
                throw new AgentProtocolException("MODEL_INVALID_FINAL");
            return;
        }
        if (response.FinishReason != "tool_calls") throw new AgentProtocolException("MODEL_INVALID_FINISH");
        var seen = new HashSet<string>(previousCallIds, StringComparer.Ordinal);
        ValidateCalls(response.ToolCalls, seen);
    }

    public static void ValidateTranscript(ImmutableArray<ModelMessage> messages)
    {
        if (messages.IsDefaultOrEmpty) throw new AgentProtocolException("MODEL_EMPTY_REQUEST");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            ValidateMessage(message);
            if (message.Role == ModelMessageRole.Tool) throw new AgentProtocolException("MODEL_ORPHAN_RESULT");
            if (message.ToolCalls.IsEmpty) continue;
            ValidateCalls(message.ToolCalls, seen);
            foreach (var call in message.ToolCalls)
            {
                if (++index >= messages.Length) throw new AgentProtocolException("MODEL_MISSING_RESULT");
                var result = messages[index];
                ValidateMessage(result);
                if (result.Role != ModelMessageRole.Tool || result.ToolCallId != call.CallId)
                    throw new AgentProtocolException("MODEL_MISPAIRED_RESULT");
            }
        }
    }

    private static void ValidateMessage(ModelMessage message)
    {
        if (message is null || !Enum.IsDefined(message.Role) || message.Content is null
            || Encoding.UTF8.GetByteCount(message.Content) > MaxContentBytes || message.ToolCalls.IsDefault)
            throw new AgentProtocolException("MODEL_INVALID_MESSAGE");
        if (message.Role != ModelMessageRole.Assistant && !message.ToolCalls.IsEmpty)
            throw new AgentProtocolException("MODEL_INVALID_MESSAGE_ROLE");
        if ((message.Role == ModelMessageRole.Tool) != (message.ToolCallId is not null))
            throw new AgentProtocolException("MODEL_INVALID_RESULT_ID");
        if (message.ToolCallId is not null && !IsIdentifier(message.ToolCallId, 128))
            throw new AgentProtocolException("MODEL_INVALID_RESULT_ID");
    }

    private static void ValidateCalls(ImmutableArray<ModelToolCall> calls, HashSet<string> seen)
    {
        if (calls.Length > MaxCalls) throw new AgentProtocolException("MODEL_TOO_MANY_CALLS");
        var bytes = 0;
        foreach (var call in calls)
        {
            if (call is null || !IsIdentifier(call.CallId, 128) || !IsIdentifier(call.Name, 64)
                || call.ArgumentsJson is null) throw new AgentProtocolException("MODEL_INVALID_CALL");
            if (!seen.Add(call.CallId)) throw new AgentProtocolException("MODEL_DUPLICATE_CALL_ID");
            var size = Encoding.UTF8.GetByteCount(call.ArgumentsJson);
            if (size > MaxArgumentsBytes || (bytes += size) > MaxBatchArgumentsBytes)
                throw new AgentProtocolException("MODEL_ARGUMENTS_TOO_LARGE");
        }
    }

    private static bool IsIdentifier(string value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsWhiteSpace)
        && !value.Any(char.IsControl);
}
