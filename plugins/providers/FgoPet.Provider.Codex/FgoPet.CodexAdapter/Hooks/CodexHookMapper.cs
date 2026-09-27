using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;

namespace FgoPet.CodexAdapter.Hooks;

public enum CodexHookKind
{
    Started,
    Resumed,
    Attention,
    Failed,
    Cancelled,
}

public sealed record CodexHookObservation(string TaskId, long Sequence, CodexHookKind Kind, string? Summary = null);

public static class CodexHookMapper
{
    public static AgentEventMessage Map(CodexHookObservation observation, string sourceType, string sourceInstance)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Sequence < 1) throw new ArgumentOutOfRangeException(nameof(observation));
        var message = new AgentEventMessage(
            sourceType,
            sourceInstance,
            observation.TaskId,
            observation.Sequence,
            observation.Kind switch
            {
                CodexHookKind.Started => "task_started",
                CodexHookKind.Resumed => "task_resumed",
                CodexHookKind.Attention => "attention_required",
                CodexHookKind.Failed => "task_failed",
                CodexHookKind.Cancelled => "task_cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(observation)),
            },
            DateTimeOffset.UtcNow,
            Summary: observation.Summary);
        return AgentPayloadSanitizer.Sanitize(message);
    }
}
