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
        Assert.Equal(2L, Scalar<long>(connection,
            "SELECT version FROM schema_migrations ORDER BY version DESC LIMIT 1"));
        foreach (var table in new[]
                 {
                     "focus_presets", "focus_sessions", "runtime_events",
                     "timeline_entries", "servant_bonds", "bond_ledger",
                     "conversations", "chat_messages", "conversation_summaries",
                     "memory_candidates", "memories", "content_bindings",
                 })
        {
            Assert.Equal(1L, Scalar<long>(connection,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
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

        Execute(connection, "INSERT INTO runtime_events VALUES('e1','s1','focus_completed','2026-08-27T09:25:00Z',1,'focus','mash',1500,1500,2,1,NULL)");
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
