using FgoPet.Core.Focus;
using FgoPet.Core.Timeline;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Focus;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Timeline;

public sealed class SqliteTimelineRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteTimelineRepository(RuntimeDatabase database) => _database = database;

    public void Insert(TimelineEntry entry, SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        if (connection is null)
        {
            using var owned = _database.Open();
            Insert(entry, owned, null);
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO timeline_entries(
              entry_id, source_event_id, occurred_at_utc, type, servant_id, elapsed_seconds, effective_seconds, bond_level)
            VALUES($id, $source, $at, $type, $servant, $elapsed, $effective, $level)
            """;
        command.Parameters.AddWithValue("$id", entry.EntryId);
        command.Parameters.AddWithValue("$source", entry.SourceEventId);
        command.Parameters.AddWithValue("$at", entry.OccurredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$type", entry.Type);
        command.Parameters.AddWithValue("$servant", entry.ServantId);
        command.Parameters.AddWithValue("$elapsed", entry.ElapsedSeconds);
        command.Parameters.AddWithValue("$effective", entry.EffectiveSeconds);
        command.Parameters.AddWithValue("$level", (object?)entry.BondLevel ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Projects the requested local date's [00:00, next 00:00) window to UTC before
    /// querying; no SQLite localtime is ever used. Boundaries are formatted as
    /// DateTimeOffset so every stored timestamp uses the same +00:00 round-trip form.
    /// </summary>
    public IReadOnlyList<TimelineEntry> QueryToday(DateOnly localDate, TimeZoneInfo zone, string servantId)
    {
        var startLocal = new DateTime(localDate, new TimeOnly(0, 0));
        var startUtc = new DateTimeOffset(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), DateTimeKind.Utc));
        var endUtc = new DateTimeOffset(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(startLocal.AddDays(1), zone), DateTimeKind.Utc));

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entry_id, source_event_id, occurred_at_utc, type, servant_id, elapsed_seconds, effective_seconds, bond_level
            FROM timeline_entries
            WHERE servant_id = $servant AND occurred_at_utc >= $start AND occurred_at_utc < $end
            ORDER BY occurred_at_utc DESC
            """;
        command.Parameters.AddWithValue("$servant", servantId);
        command.Parameters.AddWithValue("$start", startUtc.ToString("O"));
        command.Parameters.AddWithValue("$end", endUtc.ToString("O"));
        using var reader = command.ExecuteReader();
        var entries = new List<TimelineEntry>();
        while (reader.Read())
        {
            entries.Add(new TimelineEntry(
                reader.GetString(0),
                reader.GetString(1),
                SqliteFocusRepository.ParseUtc(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }

        return entries;
    }
}
