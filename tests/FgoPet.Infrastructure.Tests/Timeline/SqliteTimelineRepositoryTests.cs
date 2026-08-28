using FgoPet.Core.Focus;
using FgoPet.Core.Timeline;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Timeline;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Timeline;

public sealed class SqliteTimelineRepositoryTests : IDisposable
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private readonly RuntimeDatabase _database;
    private readonly SqliteTimelineRepository _repository;

    public SqliteTimelineRepositoryTests()
    {
        _database = new RuntimeDatabase(Path.Combine(Path.GetTempPath(), $"fgo-timeline-{Guid.NewGuid():N}.db"));
        new RuntimeDatabaseMigrator(_database).Migrate();
        _repository = new SqliteTimelineRepository(_database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _database.DatabasePath + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void QueryToday_splits_entries_on_the_local_midnight_boundary()
    {
        // 15:59:59Z and 16:00:00Z are 23:59:59 and next-day 00:00:00 in Shanghai.
        Insert("entry-before", DateTimeOffset.Parse("2026-08-27T15:59:59Z"));
        Insert("entry-after", DateTimeOffset.Parse("2026-08-27T16:00:00Z"));

        var day1 = _repository.QueryToday(new DateOnly(2026, 8, 27), Shanghai, "servant-mash");
        var day2 = _repository.QueryToday(new DateOnly(2026, 8, 28), Shanghai, "servant-mash");

        var day1Ids = day1.Select(entry => entry.EntryId).ToArray();
        var day2Ids = day2.Select(entry => entry.EntryId).ToArray();

        Assert.Equal(new[] { "entry-before" }, day1Ids);
        Assert.Equal(new[] { "entry-after" }, day2Ids);
    }

    [Fact]
    public void QueryToday_filters_by_servant()
    {
        Insert("entry-mine", DateTimeOffset.Parse("2026-08-27T09:00:00Z"), "servant-mash");
        Insert("entry-other", DateTimeOffset.Parse("2026-08-27T09:00:00Z"), "servant-other");

        var entries = _repository.QueryToday(new DateOnly(2026, 8, 27), Shanghai, "servant-mash");

        Assert.Equal(new[] { "entry-mine" }, entries.Select(entry => entry.EntryId).ToArray());
    }

    [Fact]
    public void QueryToday_orders_newest_first_and_reads_bond_level()
    {
        Insert("entry-early", DateTimeOffset.Parse("2026-08-27T01:00:00Z"), bondLevel: null);
        Insert("entry-late", DateTimeOffset.Parse("2026-08-27T02:00:00Z"), bondLevel: 3);

        var entries = _repository.QueryToday(new DateOnly(2026, 8, 27), TimeZoneInfo.Utc, "servant-mash");

        Assert.Equal(new[] { "entry-late", "entry-early" }, entries.Select(entry => entry.EntryId).ToArray());
        Assert.Equal(3, entries[0].BondLevel);
        Assert.Null(entries[1].BondLevel);
    }

    private void Insert(string entryId, DateTimeOffset occurredAtUtc, string servantId = "servant-mash", int? bondLevel = null)
    {
        // The timeline projection carries an FK to its source event; insert it first.
        var events = new FgoPet.Infrastructure.Events.SqliteEventStore(_database);
        events.TryInsert(new Core.Events.RuntimeEvent(
            $"event-{entryId}",
            "session-1",
            "focus_completed",
            occurredAtUtc,
            1,
            FocusPhase.Focus,
            servantId,
            ElapsedSeconds: 1_500,
            EffectiveSeconds: 1_500,
            Priority: 2));

        _repository.Insert(new TimelineEntry(
            entryId,
            $"event-{entryId}",
            occurredAtUtc,
            "focus_completed",
            servantId,
            ElapsedSeconds: 1_500,
            EffectiveSeconds: 1_500,
            bondLevel));
    }
}
