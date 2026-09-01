namespace FgoPet.Core.Agents;

public enum AgentEventApplyResult
{
    Applied,
    AlreadyApplied,
    IgnoredStale,
}

public sealed record PersistedAgentConnection(
    string SourceType,
    string DisplayName,
    string Version,
    bool Enabled,
    DateTimeOffset? LastEventAtUtc,
    int PendingCount,
    AgentCapabilities Capabilities);

public interface IAgentRepository
{
    void SaveExecution(AgentExecution execution);
    AgentExecution? GetExecution(string id);
    AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId);
    IReadOnlyList<AgentExecution> ListNonTerminalExecutions();
    IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit);
    bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence);
    void SaveArchiveBatch(AgentArchiveBatch batch);
    AgentArchiveBatch? GetArchiveBatch(string batchId);
    IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches();
    AgentEventApplyResult ApplyEvent(AgentEvent agentEvent);
    void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets);
    IReadOnlyList<PersistedAgentConnection> ListConnections();
}
