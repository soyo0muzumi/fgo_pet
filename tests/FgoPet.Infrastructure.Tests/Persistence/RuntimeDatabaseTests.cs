using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class RuntimeDatabaseTests : IDisposable
{
    private readonly string _path;

    public RuntimeDatabaseTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"fgo-runtime-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void Migrate_creates_schema_version_one_and_is_repeatable()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        new RuntimeDatabaseMigrator(database).Migrate();

        using var connection = database.Open();
        Assert.Equal(7L, Scalar<long>(connection,
            "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1"));
        foreach (var table in new[]
                 {
                     "focus_presets", "focus_sessions", "runtime_events",
                     "timeline_entries", "servant_bonds", "bond_ledger",
                     "conversations", "chat_messages", "conversation_summaries",
                     "memory_candidates", "memories", "content_bindings",
                     "todo_items", "agent_executions", "agent_event_receipts",
                     "agent_connections", "agent_project_targets", "work_archives", "work_archive_items",
                     "long_work_archives", "agent_archive_batches", "agent_archive_items",
                 })
        {
            Assert.Equal(1L, Scalar<long>(connection,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }
    }

    [Fact]
    public void Migrate_upgrades_the_prior_agent_schema_with_reconciliation_and_archive_tables()
    {
        var database = new RuntimeDatabase(_path);
        using (var connection = database.Open())
        {
            Execute(connection, "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_at_utc TEXT NOT NULL)");
            Execute(connection, "INSERT INTO schema_migrations(version, applied_at_utc) VALUES(6, '2026-08-30T00:00:00Z')");
            Execute(connection, """
                CREATE TABLE agent_executions(
                  execution_id TEXT PRIMARY KEY,
                  todo_id TEXT NOT NULL,
                  source_type TEXT NOT NULL,
                  source_instance TEXT NOT NULL,
                  task_id TEXT NOT NULL,
                  dispatch_request_id TEXT NOT NULL UNIQUE,
                  status TEXT NOT NULL CHECK(status IN ('dispatching','active','attention','completed','failed','cancelled')),
                  started_at_utc TEXT NULL,
                  updated_at_utc TEXT NOT NULL,
                  ended_at_utc TEXT NULL,
                  UNIQUE(source_type, source_instance, task_id));
                CREATE INDEX ix_agent_executions_todo_current
                  ON agent_executions(todo_id, updated_at_utc DESC);
                CREATE TABLE agent_event_receipts(
                  source_type TEXT NOT NULL,
                  source_instance TEXT NOT NULL,
                  task_id TEXT NOT NULL,
                  sequence INTEGER NOT NULL CHECK(sequence > 0),
                  event_type TEXT NOT NULL,
                  occurred_at_utc TEXT NOT NULL,
                  is_private INTEGER NOT NULL CHECK(is_private IN (0,1)),
                  PRIMARY KEY(source_type, source_instance, task_id, sequence));
                CREATE INDEX ix_agent_event_receipts_task
                  ON agent_event_receipts(source_type, source_instance, task_id, sequence DESC);
                """);
            Execute(connection, """
                INSERT INTO agent_executions(
                  execution_id, todo_id, source_type, source_instance, task_id, dispatch_request_id,
                  status, started_at_utc, updated_at_utc, ended_at_utc)
                VALUES(
                  'legacy-execution', 'legacy-todo', 'codex', 'legacy-instance', 'legacy-task', 'legacy-dispatch',
                  'failed', '2026-08-30T08:01:00.0000000+00:00', '2026-08-30T08:03:00.0000000+00:00', '2026-08-30T08:03:00.0000000+00:00');
                INSERT INTO agent_event_receipts(
                  source_type, source_instance, task_id, sequence, event_type, occurred_at_utc, is_private)
                VALUES('codex', 'legacy-instance', 'legacy-task', 2, 'task_failed', '2026-08-30T08:03:00.0000000+00:00', 0);
                """);
        }

        new RuntimeDatabaseMigrator(database).Migrate();

        using var verify = database.Open();
        Assert.Equal(7L, Scalar<long>(verify, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(1L, Scalar<long>(verify,
            "SELECT COUNT(*) FROM pragma_table_info('agent_executions') WHERE name='previous_execution_id'"));
        Assert.Equal(1L, Scalar<long>(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_archive_batches'"));
        Assert.Equal(1L, Scalar<long>(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_archive_items'"));
        using (var execution = verify.CreateCommand())
        {
            execution.CommandText = """
                SELECT execution_id, todo_id, source_type, source_instance, task_id, dispatch_request_id,
                       status, started_at_utc, updated_at_utc, ended_at_utc, previous_execution_id
                FROM agent_executions
                WHERE execution_id='legacy-execution'
                """;
            using var reader = execution.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("legacy-execution", reader.GetString(0));
            Assert.Equal("legacy-todo", reader.GetString(1));
            Assert.Equal("codex", reader.GetString(2));
            Assert.Equal("legacy-instance", reader.GetString(3));
            Assert.Equal("legacy-task", reader.GetString(4));
            Assert.Equal("legacy-dispatch", reader.GetString(5));
            Assert.Equal("failed", reader.GetString(6));
            Assert.Equal("2026-08-30T08:01:00.0000000+00:00", reader.GetString(7));
            Assert.Equal("2026-08-30T08:03:00.0000000+00:00", reader.GetString(8));
            Assert.Equal("2026-08-30T08:03:00.0000000+00:00", reader.GetString(9));
            Assert.True(reader.IsDBNull(10));
        }
        using (var receipt = verify.CreateCommand())
        {
            receipt.CommandText = """
                SELECT source_type, source_instance, task_id, sequence, event_type, occurred_at_utc, is_private
                FROM agent_event_receipts
                WHERE source_type='codex' AND source_instance='legacy-instance' AND task_id='legacy-task' AND sequence=2
                """;
            using var reader = receipt.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("codex", reader.GetString(0));
            Assert.Equal("legacy-instance", reader.GetString(1));
            Assert.Equal("legacy-task", reader.GetString(2));
            Assert.Equal(2L, reader.GetInt64(3));
            Assert.Equal("task_failed", reader.GetString(4));
            Assert.Equal("2026-08-30T08:03:00.0000000+00:00", reader.GetString(5));
            Assert.Equal(0, reader.GetInt32(6));
        }
        foreach (var column in new[] { "batch_id", "created_at_utc", "state", "batch_sha256", "safe_error", "completed_at_utc" })
        {
            Assert.Equal(1L, Scalar<long>(verify,
                $"SELECT COUNT(*) FROM pragma_table_info('agent_archive_batches') WHERE name='{column}'"));
        }
        foreach (var column in new[] { "batch_id", "execution_id", "source_type", "source_instance", "task_id", "dispatch_request_id", "final_sequence", "final_status", "ended_at_utc", "summary_sha256" })
        {
            Assert.Equal(1L, Scalar<long>(verify,
                $"SELECT COUNT(*) FROM pragma_table_info('agent_archive_items') WHERE name='{column}'"));
        }
    }

    [Fact]
    public void Open_enables_foreign_keys_wal_and_busy_timeout()
    {
        var database = new RuntimeDatabase(_path);
        using var connection = database.Open();

        Assert.Equal(1L, Scalar<long>(connection, "PRAGMA foreign_keys"));
        Assert.Equal("wal", Scalar<string>(connection, "PRAGMA journal_mode"));
        Assert.Equal(5000L, Scalar<long>(connection, "PRAGMA busy_timeout"));
    }

    [Fact]
    public void Enforces_partial_unique_index_on_the_current_session()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        using var connection = database.Open();

        Execute(connection, "INSERT INTO focus_sessions VALUES('s1','focusing',1500,300,4,1,'focus',1500,0,'mash','2026-08-27T09:00:00Z','2026-08-27T09:00:00Z',1)");

        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO focus_sessions VALUES('s2','paused_focus',1500,300,4,1,'focus',1400,100,'mash','2026-08-27T09:00:00Z','2026-08-27T09:05:00Z',1)"));
        // Multiple non-current rows are allowed.
        Execute(connection, "INSERT INTO focus_sessions VALUES('s3','idle',1500,300,4,1,'focus',0,0,'mash','2026-08-26T09:00:00Z','2026-08-26T09:25:00Z',0)");
        Execute(connection, "INSERT INTO focus_sessions VALUES('s4','idle',1500,300,4,1,'focus',0,0,'mash','2026-08-25T09:00:00Z','2026-08-25T09:25:00Z',0)");
        Assert.Equal(3L, Scalar<long>(connection, "SELECT COUNT(*) FROM focus_sessions"));
    }

    [Fact]
    public void Foreign_keys_cascade_checks_apply_to_timeline_and_ledger()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        using var connection = database.Open();

        Execute(connection, "INSERT INTO runtime_events(event_id, session_id, type, occurred_at_utc, cycle_number, phase, servant_id, elapsed_seconds, effective_seconds, priority, schema_version, payload_json) VALUES('e1','s1','focus_completed','2026-08-27T09:25:00Z',1,'focus','mash',1500,1500,2,1,NULL)");
        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO timeline_entries VALUES('t1','missing-event','2026-08-27T09:25:00Z','focus_completed','mash',1500,1500,NULL)"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO bond_ledger VALUES('l1','missing-event','mash',1500,'2026-08-27T09:25:00Z')"));
    }

    [Fact]
    public void A_failed_migration_leaves_no_migration_row()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        using var connection = database.Open();
        var before = Scalar<long>(connection, "SELECT MAX(version) FROM schema_migrations");

        var badScript = new Migration(before + 1, "CREATE TABLE doomed(id INTEGER); SELECT * FROM no_such_table;");
        // Replays the migrator logic against a deliberately failing script.
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = new SqliteCommand(badScript.Sql, connection, transaction);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SqliteException)
        {
            transaction.Rollback();
        }

        Assert.Equal(before, Scalar<long>(connection, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='doomed'"));
    }

    [Fact]
    public void An_unsupported_future_version_throws_without_modifying_the_file()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();

        using (var connection = database.Open())
        {
            using var bump = connection.CreateCommand();
            bump.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES(99, '2026-08-27T00:00:00Z')";
            bump.ExecuteNonQuery();
        }

        Assert.Throws<RuntimeDatabaseVersionException>(() => new RuntimeDatabaseMigrator(database).Migrate());

        using var verify = database.Open();
        Assert.Equal(99L, Scalar<long>(verify, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(0L, Scalar<long>(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='future_table'"));
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
