using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Events;

/// <summary>Persists runtime events; event IDs are primary keys, so re-inserts are no-ops.</summary>
public sealed class SqliteEventStore
{
    private readonly RuntimeDatabase _database;

    public SqliteEventStore(RuntimeDatabase database) => _database = database;

    /// <summary>Idempotent insert: returns true only when the event is newly stored.</summary>
    public bool TryInsert(RuntimeEvent runtimeEvent, SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        if (connection is null)
        {
            using var owned = _database.Open();
            return TryInsert(runtimeEvent, owned, null);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO runtime_events(
              event_id, session_id, type, occurred_at_utc, cycle_number, phase, servant_id,
              elapsed_seconds, effective_seconds, priority, schema_version, payload_json)
            VALUES($id, $session, $type, $at, $cycle, $phase, $servant, $elapsed, $effective, $priority, $schema, $payload)
            """;
        command.Parameters.AddWithValue("$id", runtimeEvent.EventId);
        command.Parameters.AddWithValue("$session", runtimeEvent.SessionId);
        command.Parameters.AddWithValue("$type", runtimeEvent.Type);
        command.Parameters.AddWithValue("$at", runtimeEvent.OccurredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$cycle", runtimeEvent.CycleNumber);
        command.Parameters.AddWithValue("$phase", FocusPhaseKeys.Key(runtimeEvent.Phase));
        command.Parameters.AddWithValue("$servant", runtimeEvent.ServantId);
        command.Parameters.AddWithValue("$elapsed", runtimeEvent.ElapsedSeconds);
        command.Parameters.AddWithValue("$effective", runtimeEvent.EffectiveSeconds);
        command.Parameters.AddWithValue("$priority", runtimeEvent.Priority);
        command.Parameters.AddWithValue("$schema", runtimeEvent.SchemaVersion);
        command.Parameters.AddWithValue("$payload", (object?)runtimeEvent.PayloadJson ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }
}
