// Work owns archive drafts. The namespace is retained for source compatibility;
// this contract is compiled by Core, not the Archives desktop implementation.
namespace FgoPet.App.Archives;

public sealed record ArchiveDraft(
    string ArchiveId,
    string SourceType,
    IReadOnlyList<string> CoveredTodoKeys,
    DateOnly ArchiveDate,
    string Title,
    DateOnly? StartedOn,
    DateOnly? CompletedOn,
    string Summary,
    IReadOnlyList<string> Outcomes,
    string ModelInput)
{
    public int CoveredTodoCount => CoveredTodoKeys.Count;
}

/// <summary>
/// Confirms an existing Work-owned draft after explicit user action. The caller may submit
/// the card's edited title and summary; creation, persistence and covered-Todo cleanup remain
/// owned by Work. Merely constructing or displaying a draft must not invoke confirmation.
/// </summary>
public interface IArchiveDraftConfirmation
{
    void Confirm(ArchiveDraft draft);
}
