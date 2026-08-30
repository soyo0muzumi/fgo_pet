using FgoPet.Core.Todo;

namespace FgoPet.Core.Agents;

public sealed record AgentGatewayStatus(
    bool IsConnected,
    string ProtocolVersion,
    DateTimeOffset? LastEventAtUtc,
    int PendingCount);

public sealed record AgentDispatchRequest(
    string DispatchRequestId,
    string TodoId,
    string Title,
    string? Description,
    TodoPriority Priority,
    DateTimeOffset? DueAt,
    string SourceType,
    string TargetId);

public enum AgentDispatchStatus
{
    Accepted,
    AlreadyApplied,
    Offline,
    Failed,
}

public sealed record AgentDispatchResult(AgentDispatchStatus Status, string DispatchRequestId, string? SafeError = null);

public sealed record AgentOpenTaskRequest(string SourceType, string SourceInstance, string TaskId);

public enum AgentOpenTaskStatus
{
    Exact,
    AppOnly,
    Unsupported,
    Offline,
}

public sealed record AgentOpenTaskResult(AgentOpenTaskStatus Status, string? SafeError = null);

public interface IAgentGateway
{
    bool IsConnected { get; }
    Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default);
    Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(
        IReadOnlyList<AgentExecution> knownExecutions,
        CancellationToken cancellationToken = default);
}
