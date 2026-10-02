namespace FgoPet.App.Archives;

public sealed record LongArchiveSummary(
    string SummaryId,
    string Title,
    string Summary,
    IReadOnlyList<string> CoveredArchiveIds,
    DateTimeOffset CreatedAt);

public sealed record LongArchiveDraft(
    string SummaryId,
    string Title,
    string Summary,
    IReadOnlyList<string> CoveredArchiveIds,
    string ModelInput)
{
    public int CoveredArchiveCount => CoveredArchiveIds.Count;
}

public interface ILongArchiveSummaryStore
{
    void Save(LongArchiveSummary summary);
    IReadOnlyList<LongArchiveSummary> List();
    void Delete(string summaryId);
}

public sealed class MemoryLongArchiveSummaryStore : ILongArchiveSummaryStore
{
    private readonly List<LongArchiveSummary> _items = new();
    public IReadOnlyList<LongArchiveSummary> Items => _items;
    public void Save(LongArchiveSummary summary) { _items.RemoveAll(item => item.SummaryId == summary.SummaryId); _items.Add(summary); }
    public IReadOnlyList<LongArchiveSummary> List() => _items.ToArray();
    public void Delete(string summaryId) => _items.RemoveAll(item => item.SummaryId == summaryId);
}

public sealed class LongArchiveService
{
    private readonly ILongArchiveSummaryStore _store;
    private readonly TimeProvider _time;
    private readonly FgoPet.Core.Archives.IWorkArchiveRepository? _repository;

    public LongArchiveService(
        ILongArchiveSummaryStore store,
        TimeProvider time,
        FgoPet.Core.Archives.IWorkArchiveRepository? repository = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _repository = repository;
    }

    public LongArchiveDraft CreateDraft(IReadOnlyList<FgoPet.Core.Archives.WorkArchive> archives, string title, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (archives.Count == 0) throw new ArgumentException("At least one work archive is required.", nameof(archives));
        var covered = archives.Select(archive => archive.ArchiveId).Distinct(StringComparer.Ordinal).ToArray();
        var input = string.Join(Environment.NewLine, archives.Select(archive => $"- {archive.Summary}"));
        return new LongArchiveDraft(
            "long-archive-" + Guid.NewGuid().ToString("N"),
            title.Trim(),
            string.IsNullOrWhiteSpace(summary) ? input : summary.Trim(),
            covered,
            input);
    }

    public LongArchiveSummary Confirm(LongArchiveDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var summary = new LongArchiveSummary(draft.SummaryId, draft.Title, draft.Summary, draft.CoveredArchiveIds, _time.GetUtcNow());
        _store.Save(summary);
        _repository?.SaveLongArchive(new FgoPet.Core.Archives.LongWorkArchive(
            summary.SummaryId,
            summary.Title,
            summary.Summary,
            summary.CoveredArchiveIds,
            summary.CreatedAt));
        foreach (var old in ExistingSummaries()
            .Where(item => item.SummaryId != summary.SummaryId && draft.CoveredArchiveIds.Contains(item.SummaryId, StringComparer.Ordinal))
            .ToArray())
        {
            _store.Delete(old.SummaryId);
            _repository?.DeleteLongArchive(old.SummaryId);
        }

        return summary;
    }

    private IReadOnlyList<LongArchiveSummary> ExistingSummaries()
    {
        var fromRepository = _repository?.ListLongArchives().Select(item => new LongArchiveSummary(
            item.ArchiveId,
            item.Title,
            item.Summary,
            item.CoveredArchiveIds,
            item.CreatedAt)) ?? Array.Empty<LongArchiveSummary>();
        return _store.List()
            .Concat(fromRepository)
            .GroupBy(item => item.SummaryId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }
}
