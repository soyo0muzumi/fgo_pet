using FgoPet.Core.Dialogue;

namespace FgoPet.Core.Packs;

public enum KnowledgeKind
{
    Profile,
    Story,
}

public sealed record KnowledgeEntry
{
    private static readonly IReadOnlySet<string> KnownApprovals =
        new HashSet<string>(StringComparer.Ordinal) { "approved", "pending", "rejected" };

    public KnowledgeEntry(
        string id,
        string servantId,
        string topic,
        string summary,
        string approval,
        KnowledgeKind kind = KnowledgeKind.Profile,
        string? appearanceId = null,
        string? sourceLocator = null,
        int? rank = null)
    {
        Id = Phase3Validation.Id(id, nameof(id));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        Topic = Phase3Validation.Text(topic, nameof(topic), 256);
        Summary = Phase3Validation.Text(summary, nameof(summary), 4_000);
        Approval = Phase3Validation.Id(approval, nameof(approval), 32).ToLowerInvariant();
        if (!KnownApprovals.Contains(Approval))
        {
            throw new ArgumentException("approval must be approved, pending, or rejected.", nameof(approval));
        }

        Kind = kind;
        AppearanceId = string.IsNullOrWhiteSpace(appearanceId)
            ? null
            : Phase3Validation.Id(appearanceId, nameof(appearanceId));
        SourceLocator = string.IsNullOrWhiteSpace(sourceLocator)
            ? null
            : Phase3Validation.Text(sourceLocator, nameof(sourceLocator), 512);
        if (rank is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        Rank = rank;
    }

    public string Id { get; }
    public string ServantId { get; }
    public string Topic { get; }
    public string Summary { get; }
    public string Approval { get; }
    public KnowledgeKind Kind { get; }
    public string? AppearanceId { get; }
    public string? SourceLocator { get; }
    public int? Rank { get; }
    public bool IsApproved => string.Equals(Approval, "approved", StringComparison.Ordinal);
}
