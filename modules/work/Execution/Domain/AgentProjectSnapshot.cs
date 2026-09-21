namespace FgoPet.Core.Agents;

public sealed record AgentProjectSnapshot
{
    public AgentProjectSnapshot(
        string snapshotId,
        string targetId,
        string projectName,
        IReadOnlyList<string>? branches,
        string? currentBranch,
        string? revision,
        string access,
        string source,
        string? contextVersion,
        DateTimeOffset capturedAtUtc)
    {
        SnapshotId = AgentIdentityValidation.Id(snapshotId, nameof(snapshotId));
        TargetId = AgentIdentityValidation.Id(targetId, nameof(targetId));
        ProjectName = AgentIdentityValidation.Id(projectName, nameof(projectName), 256);
        Branches = (branches ?? Array.Empty<string>())
            .Select(branch => AgentIdentityValidation.Id(branch, nameof(branch), 256))
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        CurrentBranch = OptionalId(currentBranch, nameof(currentBranch));
        Revision = OptionalId(revision, nameof(revision));
        Access = AgentIdentityValidation.Id(access, nameof(access), 128);
        Source = AgentIdentityValidation.Id(source, nameof(source), 128);
        ContextVersion = OptionalId(contextVersion, nameof(contextVersion), 256);
        CapturedAtUtc = capturedAtUtc;
    }

    public string SnapshotId { get; }
    public string TargetId { get; }
    public string ProjectName { get; }
    public IReadOnlyList<string> Branches { get; }
    public string? CurrentBranch { get; }
    public string? Revision { get; }
    public string Access { get; }
    public string Source { get; }
    public string? ContextVersion { get; }
    public DateTimeOffset CapturedAtUtc { get; }

    public bool HasContextVersion => !string.IsNullOrWhiteSpace(ContextVersion);

    public static AgentProjectSnapshot Create(AgentTargetDescriptor target, DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(target);
        var branches = target.Branches
            .OrderBy(branch => branch, StringComparer.Ordinal)
            .ToArray();
        var material = string.Join("\n", new[]
        {
            target.TargetId,
            target.ProjectName,
            string.Join("\n", branches),
            target.CurrentBranch ?? string.Empty,
            target.Revision ?? string.Empty,
            target.Access,
            target.Source,
            target.ContextVersion ?? string.Empty,
        });
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

        return new AgentProjectSnapshot(
            "snapshot-" + digest[..24],
            target.TargetId,
            target.ProjectName,
            branches,
            target.CurrentBranch,
            target.Revision,
            target.Access,
            target.Source,
            target.ContextVersion,
            capturedAtUtc);
    }

    private static string? OptionalId(string? value, string parameterName, int maxLength = 256) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : AgentIdentityValidation.Id(value, parameterName, maxLength);
}
