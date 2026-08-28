using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

/// <summary>Raised when the on-disk schema version is newer than this build supports.</summary>
public sealed class RuntimeDatabaseVersionException(long foundVersion, long maxSupportedVersion)
    : InvalidOperationException($"Runtime database schema version {foundVersion} is newer than the supported version {maxSupportedVersion}.")
{
    public long FoundVersion { get; } = foundVersion;

    public long MaxSupportedVersion { get; } = maxSupportedVersion;
}

/// <summary>
/// Ordered, transactional schema migrations. Each migration runs in one transaction
/// with its row inserted last; a failure rolls back completely. A database at a
/// version this build does not know raises <see cref="RuntimeDatabaseVersionException"/>
/// instead of touching any file.
/// </summary>
public sealed class RuntimeDatabaseMigrator
{
    private static readonly IReadOnlyList<Migration> Migrations = new Migration[]
    {
        new(1, """
            CREATE TABLE focus_presets(
              preset_id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              focus_seconds INTEGER NOT NULL CHECK(focus_seconds BETWEEN 300 AND 10800),
              break_seconds INTEGER NOT NULL CHECK(break_seconds BETWEEN 60 AND 3600),
              cycles INTEGER NOT NULL CHECK(cycles BETWEEN 1 AND 12),
              updated_at_utc TEXT NOT NULL);
            CREATE TABLE focus_sessions(
              session_id TEXT PRIMARY KEY,
              status TEXT NOT NULL,
              focus_seconds INTEGER NOT NULL,
              break_seconds INTEGER NOT NULL,
              total_cycles INTEGER NOT NULL,
              current_cycle INTEGER NOT NULL,
              phase TEXT NOT NULL,
              remaining_seconds INTEGER NOT NULL,
              phase_elapsed_seconds INTEGER NOT NULL,
              servant_id TEXT NOT NULL,
              started_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL,
              is_current INTEGER NOT NULL CHECK(is_current IN (0,1)));
            CREATE UNIQUE INDEX ux_focus_sessions_current ON focus_sessions(is_current) WHERE is_current=1;
            CREATE TABLE runtime_events(
              event_id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              type TEXT NOT NULL,
              occurred_at_utc TEXT NOT NULL,
              cycle_number INTEGER NOT NULL,
              phase TEXT NOT NULL,
              servant_id TEXT NOT NULL,
              elapsed_seconds INTEGER NOT NULL,
              effective_seconds INTEGER NOT NULL,
              priority INTEGER NOT NULL,
              schema_version INTEGER NOT NULL,
              payload_json TEXT NULL);
            CREATE TABLE timeline_entries(
              entry_id TEXT PRIMARY KEY,
              source_event_id TEXT NOT NULL UNIQUE REFERENCES runtime_events(event_id),
              occurred_at_utc TEXT NOT NULL,
              type TEXT NOT NULL,
              servant_id TEXT NOT NULL,
              elapsed_seconds INTEGER NOT NULL,
              effective_seconds INTEGER NOT NULL,
              bond_level INTEGER NULL);
            CREATE TABLE servant_bonds(
              servant_id TEXT PRIMARY KEY,
              lifetime_focus_seconds INTEGER NOT NULL,
              achieved_level INTEGER NOT NULL,
              policy_version TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL);
            CREATE TABLE bond_ledger(
              ledger_id TEXT PRIMARY KEY,
              source_event_id TEXT NOT NULL UNIQUE REFERENCES runtime_events(event_id),
              servant_id TEXT NOT NULL,
              effective_seconds INTEGER NOT NULL,
              occurred_at_utc TEXT NOT NULL);
            """),
    };

    private readonly RuntimeDatabase _database;

    public RuntimeDatabaseMigrator(RuntimeDatabase database) => _database = database;

    public void Migrate()
    {
        using var connection = _database.Open();
        var current = ReadVersion(connection);
        if (current > Migrations.Count)
        {
            throw new RuntimeDatabaseVersionException(current, Migrations.Count);
        }

        foreach (var migration in Migrations)
        {
            if (migration.Version <= current)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var script = new SqliteCommand(migration.Sql, connection, transaction))
            {
                script.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES($version, $applied)";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public static long ReadVersion(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
        if ((long)exists.ExecuteScalar()! == 0)
        {
            // Fresh file: create the bookkeeping table before any migration runs.
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL)";
            create.ExecuteNonQuery();
            return 0;
        }

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        return (long)query.ExecuteScalar()!;
    }
}

public sealed record Migration(long Version, string Sql);
