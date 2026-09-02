namespace FgoPet.Core.Agents;

public sealed record AgentTargetDescriptor(string TargetId, string DisplayName, bool IsReadOnly);

public enum AgentTargetCatalogStatus
{
    Available,
    AdapterNotInstalled,
    AdapterUnavailable,
    TimedOut,
    InvalidResponse,
}

public sealed record AgentTargetCatalogResult(
    AgentTargetCatalogStatus Status,
    IReadOnlyList<AgentTargetDescriptor> Targets,
    string? SafeError = null)
{
    public bool IsAvailable => Status == AgentTargetCatalogStatus.Available;
}

public interface IAgentTargetCatalog
{
    Task<AgentTargetCatalogResult> ListAsync(CancellationToken cancellationToken = default);
}
