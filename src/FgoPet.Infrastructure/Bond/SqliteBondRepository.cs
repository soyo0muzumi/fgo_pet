using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Focus;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Bond;

public sealed record ServantBondRow(
    string ServantId,
    long LifetimeFocusSeconds,
    int AchievedLevel,
    string PolicyVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed class SqliteBondRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteBondRepository(RuntimeDatabase database) => _database = database;

    public ServantBondRow? GetBond(string servantId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT servant_id, lifetime_focus_seconds, achieved_level, policy_version, updated_at_utc
            FROM servant_bonds WHERE servant_id = $id
            """;
        command.Parameters.AddWithValue("$id", servantId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ServantBondRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetString(3),
            SqliteFocusRepository.ParseUtc(reader.GetString(4)));
    }

    public void Upsert(ServantBondRow row, SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        if (connection is null)
        {
            using var owned = _database.Open();
            Upsert(row, owned, null);
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO servant_bonds(servant_id, lifetime_focus_seconds, achieved_level, policy_version, updated_at_utc)
            VALUES($id, $seconds, $level, $version, $updated)
            ON CONFLICT(servant_id) DO UPDATE SET
              lifetime_focus_seconds = lifetime_focus_seconds + $seconds,
              achieved_level = MAX(achieved_level, $level),
              policy_version = $version,
              updated_at_utc = $updated
            """;
        command.Parameters.AddWithValue("$id", row.ServantId);
        command.Parameters.AddWithValue("$seconds", row.LifetimeFocusSeconds);
        command.Parameters.AddWithValue("$level", row.AchievedLevel);
        command.Parameters.AddWithValue("$version", row.PolicyVersion);
        command.Parameters.AddWithValue("$updated", row.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void AppendLedger(string ledgerId, string sourceEventId, string servantId, int effectiveSeconds, DateTimeOffset occurredAtUtc, SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        if (connection is null)
        {
            using var owned = _database.Open();
            AppendLedger(ledgerId, sourceEventId, servantId, effectiveSeconds, occurredAtUtc, owned, null);
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO bond_ledger(ledger_id, source_event_id, servant_id, effective_seconds, occurred_at_utc)
            VALUES($id, $source, $servant, $seconds, $at)
            """;
        command.Parameters.AddWithValue("$id", ledgerId);
        command.Parameters.AddWithValue("$source", sourceEventId);
        command.Parameters.AddWithValue("$servant", servantId);
        command.Parameters.AddWithValue("$seconds", effectiveSeconds);
        command.Parameters.AddWithValue("$at", occurredAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }
}
