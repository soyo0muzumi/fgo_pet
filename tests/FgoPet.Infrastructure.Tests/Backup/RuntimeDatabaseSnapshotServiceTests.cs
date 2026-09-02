using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Backup;

public sealed class RuntimeDatabaseSnapshotServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fgo-snapshot-source-{Guid.NewGuid():N}.db");
    private readonly string _snapshotPath = Path.Combine(Path.GetTempPath(), $"fgo-snapshot-copy-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Creates_a_standalone_consistent_snapshot_without_wal_sidecars()
    {
        var database = CreateDatabase();
        using (var connection = database.Open())
        {
            Execute(connection, "INSERT INTO focus_presets VALUES('short','builtin',300,60,1,'2026-09-02T00:00:00Z')");
            Execute(connection, "INSERT INTO agent_executions(execution_id, todo_id, source_type, source_instance, task_id, dispatch_request_id, status, updated_at_utc, remote_task_id) VALUES('execution-1','todo-1','codex','instance-1','task-1','dispatch-1','active','2026-09-02T00:00:00Z','remote-1')");
        }

        await new RuntimeDatabaseSnapshotService(database).CreateAsync(_snapshotPath, CancellationToken.None);

        Assert.True(File.Exists(_snapshotPath));
        Assert.False(File.Exists(_snapshotPath + "-wal"));
        Assert.False(File.Exists(_snapshotPath + "-shm"));

        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        snapshot.Open();
        Assert.Equal("ok", Scalar<string>(snapshot, "PRAGMA integrity_check"));
        Assert.Equal(8L, Scalar<long>(snapshot, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal("remote-1", Scalar<string>(snapshot, "SELECT remote_task_id FROM agent_executions WHERE execution_id='execution-1'"));
        Assert.Equal(1L, Scalar<long>(snapshot, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='short'"));
    }

    [Fact]
    public async Task Cancellation_before_snapshot_does_not_create_output()
    {
        var database = CreateDatabase();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RuntimeDatabaseSnapshotService(database).CreateAsync(_snapshotPath, cancellation.Token));

        Assert.False(File.Exists(_snapshotPath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm", _snapshotPath, _snapshotPath + "-wal", _snapshotPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
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
