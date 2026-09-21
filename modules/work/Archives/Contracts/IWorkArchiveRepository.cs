namespace FgoPet.Core.Archives;

public interface IWorkArchiveRepository
{
    void Confirm(WorkArchive archive);
    WorkArchive? Get(string archiveId);
    IReadOnlyList<WorkArchive> List();
    IReadOnlyList<string> LoadCoveredTodoKeys(string archiveId);

    void SaveLongArchive(LongWorkArchive archive) => throw new NotSupportedException();
    IReadOnlyList<LongWorkArchive> ListLongArchives() => Array.Empty<LongWorkArchive>();
    void DeleteLongArchive(string archiveId) => throw new NotSupportedException();
}

public sealed record LongWorkArchive(
    string ArchiveId,
    string Title,
    string Summary,
    IReadOnlyList<string> CoveredArchiveIds,
    DateTimeOffset CreatedAt);
