namespace FgoPet.Core.Archives;

public sealed record WorkArchive
{
    public WorkArchive(
        string archiveId,
        IReadOnlyList<string> coveredTodoKeys,
        IReadOnlyList<string> sourceTypes,
        DateOnly archiveDate,
        string summary,
        DateTimeOffset createdAt)
    {
        ArchiveId = ArchiveValidation.Id(archiveId, nameof(archiveId));
        CoveredTodoKeys = ArchiveValidation.Ids(coveredTodoKeys, nameof(coveredTodoKeys));
        SourceTypes = ArchiveValidation.Ids(sourceTypes, nameof(sourceTypes));
        ArchiveDate = archiveDate;
        Summary = ArchiveValidation.Text(summary, nameof(summary), 6_000);
        CreatedAt = createdAt;
    }

    public string ArchiveId { get; }
    public IReadOnlyList<string> CoveredTodoKeys { get; }
    public IReadOnlyList<string> SourceTypes { get; }
    public DateOnly ArchiveDate { get; }
    public string Summary { get; }
    public DateTimeOffset CreatedAt { get; }
}

internal static class ArchiveValidation
{
    public static string Id(string value, string parameterName, int maxLength = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static IReadOnlyList<string> Ids(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        return values.Select(value => Id(value, parameterName)).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string Text(string value, string parameterName, int maxLength) => Id(value, parameterName, maxLength);
}
