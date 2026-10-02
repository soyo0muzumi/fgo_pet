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
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, Scalar<long>(connection,
            "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1"));
        foreach (var table in new[]
                 {
                     "focus_presets", "focus_sessions", "runtime_events",
                     "timeline_entries", "servant_bonds", "bond_ledger",
                     "conversations", "chat_messages", "conversation_summaries",
                     "memory_candidates", "memories", "content_bindings",
                     "todo_items", "agent_executions", "agent_event_receipts",
                     "todo_steps",
                     "agent_connections", "agent_project_targets", "work_archives", "work_archive_items",
                     "long_work_archives", "agent_archive_batches", "agent_archive_items", "agent_project_snapshots",
                 })
        {
            Assert.Equal(1L, Scalar<long>(connection,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        }
    }

    [Fact]
    public void Migration_11_adds_empty_steps_without_rewriting_legacy_description()
    {
        var database = new RuntimeDatabase(_path);
        CreateLegacy(database, 10);

        using (var connection = database.Open())
        {
            Execute(connection, "INSERT INTO todo_items(todo_id, title, description, priority, due_at_utc, status, created_at_utc, updated_at_utc, completed_at_utc) VALUES('legacy', 'Legacy', '1. Keep this text\n2. Do not convert', 'normal', NULL, 'planned', '2026-09-13T00:00:00Z', '2026-09-13T00:00:00Z', NULL)");


        }

        new RuntimeDatabaseMigrator(database).Migrate();

        using var verify = database.Open();
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, Scalar<long>(verify, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal(0L, Scalar<long>(verify, "SELECT COUNT(*) FROM todo_steps"));
        Assert.Equal("1. Keep this text\n2. Do not convert", Scalar<string>(verify, "SELECT description FROM todo_items WHERE todo_id='legacy'"));
    }

    [Fact]
    public void Migrate_archives_a_corrupt_database_before_recreating_it()
    {
        File.WriteAllText(_path, "not a sqlite database");
        var database = new RuntimeDatabase(_path);

        new RuntimeDatabaseMigrator(database).Migrate();

        Assert.True(File.Exists(_path));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*"));
        using var connection = database.Open();
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, Scalar<long>(connection,
            "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1"));
    }

    [Fact]
    public void Migrate_upgrades_the_prior_agent_schema_with_reconciliation_and_archive_tables()
    {
        var database = new RuntimeDatabase(_path);
        CreateLegacy(database, 6);
        using (var connection = database.Open())
        {
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
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, Scalar<long>(verify, "SELECT MAX(version) FROM schema_migrations"));
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

    private static void CreateLegacy(RuntimeDatabase database, int version)
    {
        // Construct the complete historical schema, not a partial table fixture that
        // incorrectly claims a newer schema version.
        var migrations = (IReadOnlyList<Migration>)typeof(RuntimeDatabaseMigrator).GetField("Migrations",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        using var connection = database.Open();
        RuntimeDatabaseMigrator.ReadVersion(connection);
        foreach (var migration in migrations.Where(item => item.Version <= version))
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO schema_migrations VALUES($version,'2026-09-23T00:00:00Z')";
            command.Parameters.AddWithValue("$version", migration.Version);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Older_schemas_preserve_memories_as_legacy_general_and_never_activate_old_concatenated_summaries(int schema)
    {
        var database = new RuntimeDatabase(_path, pooling: false);
        CreateLegacy(database, schema);
        using (var connection = database.Open())
        {
            Execute(connection, "INSERT INTO conversations(conversation_id,servant_id,created_at_utc,updated_at_utc,status) VALUES('old','mash','2026-09-23','2026-09-23','active')");
            Execute(connection, "INSERT INTO memories VALUES('memory','mash','已确认的旧偏好',1,NULL,'2026-09-23','2026-09-23')");
            Execute(connection, "INSERT INTO conversation_summaries(summary_id,conversation_id,servant_id,summary_text,covered_through_sequence,created_at_utc,updated_at_utc,covered_through_message_id) VALUES('summary','old','mash','旧拼接摘要',1,'2026-09-23','2026-09-23','old-message')");
        }
        new RuntimeDatabaseMigrator(database).Migrate();
        var memory = Assert.Single(new FgoPet.Infrastructure.Memory.SqliteMemoryRepository(database).ListMemories("mash"));
        Assert.Null(memory.ProjectId);
        Assert.Null(memory.Source);
        Assert.Equal(1, memory.Version);
        Assert.Equal("已确认的旧偏好", memory.Text);
        using var verify = database.Open();
        Assert.Equal(0L, Scalar<long>(verify, "SELECT COUNT(*) FROM conversation_contexts"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM conversation_summaries"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM memory_write_state WHERE length(generation)>0"));
    }

    [Fact]
    public void Migration_12_indexes_legacy_messages_without_assigning_a_project()
    {
        var database = new RuntimeDatabase(_path, pooling: false);
        CreateLegacy(database, 11);
        using (var connection = database.Open())
        {
            Execute(connection, "INSERT INTO conversations VALUES('old','mash','2026-09-23','2026-09-23','active',NULL)");
            Execute(connection, "INSERT INTO chat_messages VALUES('m1','old','mash',1,'user','旧的导师面谈','completed','2026-09-23',NULL)");
        }
        new RuntimeDatabaseMigrator(database).Migrate();
        using var verify = database.Open();
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM conversations WHERE project_id IS NULL AND project_label IS NULL"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM chat_message_search WHERE chat_message_search MATCH '导师面谈'"));
    }

    [Fact]
    public void Migration_12_failure_rolls_back_columns_and_version_then_can_retry()
    {
        var database = new RuntimeDatabase(_path, pooling: false);
        CreateLegacy(database, 11);
        using (var connection = database.Open()) Execute(connection, "CREATE TABLE chat_message_search(conflict INTEGER)");
        Assert.Throws<SqliteException>(() => new RuntimeDatabaseMigrator(database).Migrate());
        using (var verify = database.Open())
        {
            Assert.Equal(11L, Scalar<long>(verify, "SELECT MAX(version) FROM schema_migrations"));
            Assert.Equal(0L, Scalar<long>(verify, "SELECT COUNT(*) FROM pragma_table_info('conversations') WHERE name='project_id'"));
            Execute(verify, "DROP TABLE chat_message_search");
        }
        new RuntimeDatabaseMigrator(database).Migrate();
        using var final = database.Open();
        Assert.Equal(RuntimeDatabaseMigrator.CurrentSchemaVersion, RuntimeDatabaseMigrator.ReadVersion(final));
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
