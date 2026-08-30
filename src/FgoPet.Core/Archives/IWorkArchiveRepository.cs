namespace FgoPet.Core.Archives;

public interface IWorkArchiveRepository
{
    void Confirm(WorkArchive archive);
    WorkArchive? Get(string archiveId);
    IReadOnlyList<WorkArchive> List();
    IReadOnlyList<string> LoadCoveredTodoKeys(string archiveId);
}
