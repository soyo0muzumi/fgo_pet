namespace FgoPet.Core.Agents;

public sealed record AgentTargetDescriptor
{
    public AgentTargetDescriptor(
        string targetId,
        string displayName,
        bool isReadOnly,
        string? projectName = null,
        IReadOnlyList<string>? branches = null,
        string? currentBranch = null,
        string? revision = null,
        string access = "local",
        string source = "local-adapter",
        DateTimeOffset? refreshedAtUtc = null,
        string? contextVersion = null)
    {
        TargetId = AgentIdentityValidation.Id(targetId, nameof(targetId));
        DisplayName = AgentIdentityValidation.Id(displayName, nameof(displayName), 256);
        IsReadOnly = isReadOnly;
        ProjectName = OptionalText(projectName, nameof(projectName)) ?? DisplayName;
        Branches = (branches ?? Array.Empty<string>())
            .Select(branch => OptionalText(branch, nameof(branch)))
            .Where(branch => branch is not null)
            .Select(branch => branch!)
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        CurrentBranch = OptionalText(currentBranch, nameof(currentBranch));
        Revision = OptionalText(revision, nameof(revision));
        Access = OptionalText(access, nameof(access)) ?? "local";
        Source = OptionalText(source, nameof(source)) ?? "local-adapter";
        RefreshedAtUtc = refreshedAtUtc;
        ContextVersion = OptionalText(contextVersion, nameof(contextVersion));
    }

    public string TargetId { get; }
    public string DisplayName { get; }
    public bool IsReadOnly { get; }
    public string ProjectName { get; }
    public IReadOnlyList<string> Branches { get; }
    public string? CurrentBranch { get; }
    public string? Revision { get; }
    public string Access { get; }
    public string Source { get; }
    public DateTimeOffset? RefreshedAtUtc { get; }
    public string? ContextVersion { get; }

    private static string? OptionalText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException($"{parameterName} is invalid.", parameterName);
        return normalized;
    }
}

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