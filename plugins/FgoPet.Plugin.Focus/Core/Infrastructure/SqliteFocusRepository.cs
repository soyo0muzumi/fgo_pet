using FgoPet.Core.Focus;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Focus;

/// <summary>Persisted preset row (built-ins stay code constants; only custom presets persist).</summary>
public sealed record StoredFocusPreset(string PresetId, string Kind, int FocusSeconds, int BreakSeconds, int Cycles, DateTimeOffset UpdatedAtUtc);

public sealed class SqliteFocusRepository
{
    public const string CustomPresetId = "custom.last";
    public const string BuiltinPresetId = "builtin.25x4";
    public const string BuiltinLongPresetId = "builtin.50x2";

    private readonly RuntimeDatabase _database;

    public SqliteFocusRepository(RuntimeDatabase database) => _database = database;

    /// <summary>Writes one session snapshot, keeping exactly one current row.</summary>
    public void SaveSnapshot(FocusSession session)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        DemoteCurrent(connection, transaction, session.IsCurrent ? session.SessionId : null);
        UpsertSnapshot(connection, transaction, session);
        transaction.Commit();
    }

    public FocusSession? LoadCurrent()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, status, focus_seconds, break_seconds, total_cycles, current_cycle,
                   phase, remaining_seconds, phase_elapsed_seconds, servant_id, started_at_utc,
                   updated_at_utc, is_current
            FROM focus_sessions WHERE is_current = 1 LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new FocusSession(
            reader.GetString(0),
            StatusFromKey(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6) == FocusPhaseKeys.Break ? FocusPhase.Break : FocusPhase.Focus,
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetString(9),
            ParseUtc(reader.GetString(10)),
            ParseUtc(reader.GetString(11)),
            reader.GetInt32(12) == 1);
    }

    public StoredFocusPreset? LoadPreset(string presetId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preset_id, kind, focus_seconds, break_seconds, cycles, updated_at_utc
            FROM focus_presets WHERE preset_id = $id
            """;
        command.Parameters.AddWithValue("$id", presetId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StoredFocusPreset(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetInt32(4), ParseUtc(reader.GetString(5)));
    }

    public void SavePreset(StoredFocusPreset preset)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO focus_presets(preset_id, kind, focus_seconds, break_seconds, cycles, updated_at_utc)
            VALUES($id, $kind, $focus, $break, $cycles, $updated)
            ON CONFLICT(preset_id) DO UPDATE SET
              kind=$kind, focus_seconds=$focus, break_seconds=$break, cycles=$cycles, updated_at_utc=$updated
            """;
        command.Parameters.AddWithValue("$id", preset.PresetId);
        command.Parameters.AddWithValue("$kind", preset.Kind);
        command.Parameters.AddWithValue("$focus", preset.FocusSeconds);
        command.Parameters.AddWithValue("$break", preset.BreakSeconds);
        command.Parameters.AddWithValue("$cycles", preset.Cycles);
        command.Parameters.AddWithValue("$updated", preset.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    internal static void DemoteCurrent(SqliteConnection connection, SqliteTransaction transaction, string? keepSessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = keepSessionId is null
            ? "UPDATE focus_sessions SET is_current=0 WHERE is_current=1"
            : "UPDATE focus_sessions SET is_current=0 WHERE is_current=1 AND session_id <> $keep";
        if (keepSessionId is not null)
        {
            command.Parameters.AddWithValue("$keep", keepSessionId);
        }

        command.ExecuteNonQuery();
    }

    internal static void UpsertSnapshot(SqliteConnection connection, SqliteTransaction transaction, FocusSession session)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO focus_sessions(
              session_id, status, focus_seconds, break_seconds, total_cycles, current_cycle, phase,
              remaining_seconds, phase_elapsed_seconds, servant_id, started_at_utc, updated_at_utc, is_current)
            VALUES($id, $status, $focus, $break, $cycles, $cycle, $phase, $remaining, $elapsed, $servant, $started, $updated, $current)
            ON CONFLICT(session_id) DO UPDATE SET
              status=$status, focus_seconds=$focus, break_seconds=$break, total_cycles=$cycles,
              current_cycle=$cycle, phase=$phase, remaining_seconds=$remaining, phase_elapsed_seconds=$elapsed,
              servant_id=$servant, started_at_utc=$started, updated_at_utc=$updated, is_current=$current
            """;
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$status", FocusStatusKeys.Key(session.Status));
        command.Parameters.AddWithValue("$focus", session.FocusSeconds);
        command.Parameters.AddWithValue("$break", session.BreakSeconds);
        command.Parameters.AddWithValue("$cycles", session.TotalCycles);
        command.Parameters.AddWithValue("$cycle", session.CurrentCycle);
        command.Parameters.AddWithValue("$phase", FocusPhaseKeys.Key(session.Phase));
        command.Parameters.AddWithValue("$remaining", session.RemainingSeconds);
        command.Parameters.AddWithValue("$elapsed", session.PhaseElapsedSeconds);
        command.Parameters.AddWithValue("$servant", session.ServantId);
        command.Parameters.AddWithValue("$started", session.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", session.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$current", session.IsCurrent ? 1 : 0);
        command.ExecuteNonQuery();
    }

    internal static FocusStatus StatusFromKey(string key) => key switch
    {
        FocusStatusKeys.Idle => FocusStatus.Idle,
        FocusStatusKeys.Focusing => FocusStatus.Focusing,
        FocusStatusKeys.PausedFocus => FocusStatus.PausedFocus,
        FocusStatusKeys.Breaking => FocusStatus.Breaking,
        FocusStatusKeys.PausedBreak => FocusStatus.PausedBreak,
        FocusStatusKeys.Completed => FocusStatus.Completed,
        _ => throw new InvalidOperationException($"Unknown focus status '{key}'."),
    };

    internal static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.ParseExact(text, "O", null, System.Globalization.DateTimeStyles.RoundtripKind);
}
