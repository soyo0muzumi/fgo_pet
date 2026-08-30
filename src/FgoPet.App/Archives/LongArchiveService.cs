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

    public LongArchiveService(ILongArchiveSummaryStore store, TimeProvider time)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
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
        foreach (var old in _store.List().Where(item => item.SummaryId != summary.SummaryId && draft.CoveredArchiveIds.Contains(item.SummaryId, StringComparer.Ordinal)).ToArray())
        {
            _store.Delete(old.SummaryId);
        }

        return summary;
    }
}
