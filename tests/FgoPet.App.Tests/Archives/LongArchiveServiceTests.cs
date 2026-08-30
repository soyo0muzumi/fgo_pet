using FgoPet.App.Archives;
using FgoPet.Core.Archives;
using Xunit;

namespace FgoPet.App.Tests.Archives;

public sealed class LongArchiveServiceTests
{
    [Fact]
    public void Long_archive_confirmation_replaces_only_the_selected_work_summaries()
    {
        var store = new MemoryLongArchiveSummaryStore();
        var service = new LongArchiveService(store, TimeProvider.System);
        var first = new WorkArchive("archive-1", new[] { "todo-1" }, new[] { "codex" }, DateOnly.FromDateTime(DateTime.Today), "First", DateTimeOffset.UtcNow);
        var second = new WorkArchive("archive-2", new[] { "todo-2" }, new[] { "codex" }, DateOnly.FromDateTime(DateTime.Today), "Second", DateTimeOffset.UtcNow);
        var draft = service.CreateDraft(new[] { first, second }, "Bridge history");

        service.Confirm(draft);

        var saved = Assert.Single(store.Items);
        Assert.Equal("Bridge history", saved.Title);
        Assert.Equal(new[] { "archive-1", "archive-2" }, saved.CoveredArchiveIds);
    }
}
