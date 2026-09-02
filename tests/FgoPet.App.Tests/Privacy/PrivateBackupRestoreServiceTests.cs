using System;
using System.IO;
using FgoPet.App.Privacy;
using FgoPet.Core.Agents;
using FgoPet.Core.Backup;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.App.Tests.Privacy;

public sealed class PrivateBackupRestoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fgo-private-restore-{Guid.NewGuid():N}");
    private readonly string _currentRoot;
    private readonly string _sourceRoot;

    public PrivateBackupRestoreServiceTests()
    {
        _currentRoot = Path.Combine(_root, "current");
        _sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(_currentRoot);
        Directory.CreateDirectory(_sourceRoot);
    }

    [Fact]
    public async Task Restores_to_current_state_and_normalizes_active_agent_without_reexecution()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Active, "remote-source");
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);

        var runtime = new FakeAgentRuntime();
        var restore = CreateRestoreService(current, runtime);

        var result = await restore.RestoreAsync(backupPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Restored, result.Status);
        Assert.True(runtime.StopCalled);
        Assert.Equal(0, runtime.DispatchCount);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='source-row'"));
        Assert.Equal(0L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
        var execution = new SqliteAgentRepository(current.Database).GetExecution("source-execution")!;
        Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, execution.Status);
        Assert.Equal("remote-source", execution.RemoteTaskId);
        Assert.Equal("source-user", new JsonAppSettingsStore(_currentRoot).Load().UserProfile!.DisplayName);
    }

    [Fact]
    public async Task Rejects_corrupt_input_without_changing_current_database_or_settings()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var databaseBefore = File.ReadAllBytes(current.Database.DatabasePath);
        var settingsBefore = File.ReadAllBytes(new JsonAppSettingsStore(_currentRoot).Location);
        var corruptPath = Path.Combine(_root, "corrupt.fgopetbackup");
        File.WriteAllText(corruptPath, "not a backup");

        var result = await CreateRestoreService(current, new FakeAgentRuntime()).RestoreAsync(corruptPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
        Assert.Equal(BackupFailureCode.InvalidManifest, result.FailureCode);
        Assert.Equal(databaseBefore, File.ReadAllBytes(current.Database.DatabasePath));
        Assert.Equal(settingsBefore, File.ReadAllBytes(new JsonAppSettingsStore(_currentRoot).Location));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
    }

    [Fact]
    public async Task Rolls_back_when_swap_fails_after_database_replacement()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);
        var swap = new PartiallyFailingSwap();

        var result = await CreateRestoreService(current, new FakeAgentRuntime(), swap).RestoreAsync(backupPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.RolledBack, result.Status);
        Assert.Equal(BackupFailureCode.SwapFailed, result.FailureCode);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
        Assert.Equal("old-user", new JsonAppSettingsStore(_currentRoot).Load().UserProfile!.DisplayName);
    }

    [Fact]
    public async Task Maintenance_coordinator_stops_runtime_and_serializes_operations()
    {
        var runtime = new FakeAgentRuntime();
        var coordinator = new AppMaintenanceCoordinator(runtime);
        await using var first = await coordinator.EnterAsync(CancellationToken.None);
        Assert.True(runtime.StopCalled);

        var second = coordinator.EnterAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await using var secondLease = await second;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task CreateBackupAsync(State source, string backupPath)
    {
        var settings = new JsonAppSettingsStore(source.Root);
        var packages = new JsonPackIndexStore(source.Root);
        await new PrivateBackupService(
            source.Database,
            settings,
            packages,
            new RuntimeDatabaseSnapshotService(source.Database),
            new AppSettingsSnapshotCodec(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            "1.0.0").CreateAsync(backupPath, CancellationToken.None);
    }

    private PrivateBackupRestoreService CreateRestoreService(
        State current,
        FakeAgentRuntime runtime,
        IBackupStateSwapper? swapper = null)
    {
        var settings = new JsonAppSettingsStore(current.Root);
        var packages = new JsonPackIndexStore(current.Root);
        var rollbackBackup = new PrivateBackupService(
            current.Database,
            settings,
            packages,
            new RuntimeDatabaseSnapshotService(current.Database),
            new AppSettingsSnapshotCodec(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            "1.0.0");
        return new PrivateBackupRestoreService(
            current.Database,
            settings,
            packages,
            rollbackBackup,
            new PrivateBackupReader(),
            new AppMaintenanceCoordinator(runtime),
            current.Root,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            swapper);
    }

    private static State CreateState(
        string root,
        string rowId,
        string userName,
        AgentExecutionStatus status,
        string? remoteTaskId = null)
    {
        var database = new RuntimeDatabase(Path.Combine(root, "runtime.db"));
        new RuntimeDatabaseMigrator(database).Migrate();
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO focus_presets VALUES($id,'builtin',300,60,1,'2026-09-02T00:00:00Z')";
            command.Parameters.AddWithValue("$id", rowId);
            command.ExecuteNonQuery();
        }

        var at = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
        DateTimeOffset? endedAt = status == AgentExecutionStatus.Completed ? at : null;
        new SqliteAgentRepository(database).SaveExecution(new AgentExecution(
            status == AgentExecutionStatus.Completed ? "current-execution" : "source-execution",
            rowId,
            "codex",
            "instance-1",
            status == AgentExecutionStatus.Completed ? "current-task" : "source-task",
            status == AgentExecutionStatus.Completed ? "current-request" : "source-request",
            at,
            status,
            startedAt: at,
            endedAt: endedAt,
            remoteTaskId: remoteTaskId));

        var settings = new JsonAppSettingsStore(root);
        settings.Save(AppSettings.Defaults with { UserProfile = new UserProfile(userName) });
        new JsonPackIndexStore(root).Save(PackIndexV1.Empty);
        SqliteConnection.ClearAllPools();
        return new State(root, database);
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private sealed record State(string Root, RuntimeDatabase Database);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAgentRuntime : IAgentRelayRuntime
    {
        public AgentRelaySnapshot Current => AgentRelaySnapshot.Disabled;
        public event Action<AgentRelaySnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }
        public bool StopCalled { get; private set; }
        public int DispatchCount { get; private set; }
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return Task.CompletedTask;
        }

        public void Dispatch() => DispatchCount++;
    }

    private sealed class PartiallyFailingSwap : IBackupStateSwapper
    {
        public Task SwapAsync(BackupStateSwapContext context, CancellationToken cancellationToken)
        {
            File.Copy(context.StagedDatabasePath, context.CurrentDatabasePath, overwrite: true);
            throw new IOException("simulated swap failure");
        }
    }
}
