using FgoPet.Infrastructure.Persistence;

namespace FgoPet.Infrastructure.Backup;

/// <summary>
/// Marks restored non-terminal Agent executions as requiring explicit
/// reconciliation. It only changes the staged SQLite database and never calls
/// Relay, Adapter, Codex, or any network service.
/// </summary>
public static class BackupDatabaseNormalizer
{
    public static int Normalize(RuntimeDatabase stagingDatabase, DateTimeOffset? normalizedAt = null)
    {
        ArgumentNullException.ThrowIfNull(stagingDatabase);
        using var connection = stagingDatabase.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_executions
            SET status='dispatch_outcome_unknown', updated_at_utc=$updated
            WHERE status IN ('dispatching','active','attention')
            """;
        command.Parameters.AddWithValue("$updated", (normalizedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        return changed;
    }
}
