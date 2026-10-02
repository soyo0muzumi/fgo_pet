using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Privacy;
using FgoPet.Character.Settings;
using FgoPet.Core.Agents;
using FgoPet.Core.Backup;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;
using FgoPet.SettingsHost;
using FgoPet.Work.Execution.Settings;
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
        Assert.True(result.AgentPairingRequired);
        Assert.True(runtime.StopCalled);
        Assert.Equal(0, runtime.DispatchCount);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='source-row'"));
        Assert.Equal(0L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
        var execution = new SqliteAgentRepository(current.Database).GetExecution("source-execution")!;
        Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, execution.Status);
        Assert.Equal("remote-source", execution.RemoteTaskId);
        Assert.Equal("source-user", ((ICharacterSettingsStore)current.Settings).Load().UserProfile!.DisplayName);
    }

    [Fact]
    public async Task Rejects_corrupt_input_without_changing_current_database_or_settings()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var databaseBefore = File.ReadAllBytes(current.Database.DatabasePath);
        var settingsBefore = File.ReadAllBytes(current.Settings.Location);
        var corruptPath = Path.Combine(_root, "corrupt.fgopetbackup");
        File.WriteAllText(corruptPath, "not a backup");

        var result = await CreateRestoreService(current, new FakeAgentRuntime()).RestoreAsync(corruptPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
        Assert.Equal(BackupFailureCode.InvalidManifest, result.FailureCode);
        Assert.Equal(databaseBefore, File.ReadAllBytes(current.Database.DatabasePath));
        Assert.Equal(settingsBefore, File.ReadAllBytes(current.Settings.Location));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
    }

    [Fact]
    public async Task Rejects_malformed_settings_through_document_without_quarantining_or_mutating_live_settings()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);
        ReplaceSettingsMember(backupPath, "{bad");
        var databaseBefore = File.ReadAllBytes(current.Database.DatabasePath);
        var settingsBefore = File.ReadAllBytes(current.Settings.Location);
        var observingDocument = new ObservingApplicationSettingsDocument(current.Settings);

        var result = await CreateRestoreService(
            current,
            new FakeAgentRuntime(),
            settingsDocument: observingDocument).RestoreAsync(backupPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
        Assert.Equal(BackupFailureCode.SettingsInvalid, result.FailureCode);
        Assert.Equal(1, observingDocument.ValidateCount);
        Assert.Equal(databaseBefore, File.ReadAllBytes(current.Database.DatabasePath));
        Assert.Equal(settingsBefore, File.ReadAllBytes(current.Settings.Location));
        Assert.Empty(Directory.EnumerateFiles(_currentRoot, "settings.json.corrupt.*"));
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
        Assert.Equal("old-user", ((ICharacterSettingsStore)current.Settings).Load().UserProfile!.DisplayName);
    }

    [Fact]
    public async Task Maintenance_coordinator_stops_runtime_and_serializes_operations()
    {
        var runtime = new FakeAgentRuntime();
        var coordinator = new AppMaintenanceCoordinator(runtime);
        await using var first = await coordinator.EnterAsync(CancellationToken.None);
        Assert.True(runtime.StopCalled);
        Assert.Throws<InvalidOperationException>(coordinator.CompleteStartup);

        var second = coordinator.EnterAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await using var secondLease = await second;
    }

    [Fact]
    public async Task Rollback_keeps_maintenance_exclusive_until_the_result_is_ready()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);
        var maintenance = new AppMaintenanceCoordinator();
        Task<IAsyncDisposable>? competing = null;
        var enteredDuringRollback = false;
        var restore = CreateRestoreService(current, new FakeAgentRuntime(), new PartiallyFailingSwap(),
            maintenance: maintenance, safeLog: name =>
            {
                if (name != "restore.rolled_back") return;
                competing = maintenance.EnterAsync(CancellationToken.None);
                enteredDuringRollback = competing.IsCompleted;
            });

        var result = await restore.RestoreAsync(backupPath, CancellationToken.None);
        Assert.NotNull(competing);
        await using var secondLease = await competing;
        Assert.Equal(BackupRestoreStatus.RolledBack, result.Status);
        Assert.False(enteredDuringRollback);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
    }

    [Fact]
    public async Task Failed_rollback_is_not_reported_as_restored_and_keeps_recovery_materials()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);

        var result = await CreateRestoreService(current, new FakeAgentRuntime(), new RollbackBlockingSwap())
            .RestoreAsync(backupPath, CancellationToken.None);

        Assert.NotEqual(BackupRestoreStatus.RolledBack, result.Status);
        Assert.NotEqual(BackupRestoreStatus.Restored, result.Status);
        var recovery = Assert.Single(Directory.GetDirectories(_currentRoot, "*.rollback"));
        Assert.NotEmpty(Directory.GetFiles(recovery, "*.state"));

        var retry = await CreateRestoreService(current, new FakeAgentRuntime())
            .RestoreAsync(backupPath, CancellationToken.None);
        Assert.Equal(BackupRestoreStatus.RecoveryRequired, retry.Status);
        Assert.Equal(recovery, Assert.Single(Directory.GetDirectories(_currentRoot, "*.rollback")));
    }

    [Fact]
    public async Task Queued_restore_changes_no_live_data_and_uses_its_own_validated_copy_at_startup()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);
        var pending = new PendingBackupRestoreService(_currentRoot, current.Settings, new PrivateBackupReader());

        await pending.PrepareAsync(backupPath, CancellationToken.None);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
        File.Delete(backupPath);
        var result = await pending.ApplyBeforeStartupAsync(CreateRestoreService(current, new FakeAgentRuntime()), CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Restored, result!.Status);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='source-row'"));
        Assert.Null(await pending.ApplyBeforeStartupAsync(CreateRestoreService(current, new FakeAgentRuntime()), CancellationToken.None));
    }

    [Fact]
    public async Task Runtime_started_boundary_rejects_file_replacement()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var backupPath = Path.Combine(_root, "input.fgopetbackup");
        await CreateBackupAsync(source, backupPath);
        var maintenance = new AppMaintenanceCoordinator();
        maintenance.CompleteStartup();

        var result = await CreateRestoreService(current, new FakeAgentRuntime(), maintenance: maintenance)
            .RestoreAsync(backupPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
    }

    [Fact]
    public async Task Interrupted_restore_prevents_startup_and_does_not_consume_recovery_materials()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var marker = PrivateBackupRestoreService.RecoveryMarkerPath(_currentRoot);
        File.WriteAllText(marker, "synthetic interrupted restore");
        var pending = new PendingBackupRestoreService(_currentRoot, current.Settings, new PrivateBackupReader());

        var result = await pending.ApplyBeforeStartupAsync(CreateRestoreService(current, new FakeAgentRuntime()), CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.RecoveryRequired, result!.Status);
        Assert.True(File.Exists(marker));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
    }

    [Fact]
    public async Task Invalid_backup_is_never_queued_and_empty_startup_does_not_construct_restore_services()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var invalid = Path.Combine(_root, "invalid.fgopetbackup");
        File.WriteAllText(invalid, "invalid archive");
        var pending = new PendingBackupRestoreService(_currentRoot, current.Settings, new PrivateBackupReader());

        await Assert.ThrowsAsync<BackupException>(() => pending.PrepareAsync(invalid, CancellationToken.None));
        Assert.Null(await pending.ApplyBeforeStartupAsync(() => throw new InvalidOperationException("Must not start a restore"), CancellationToken.None));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_presets WHERE preset_id='old-row'"));
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
        var packages = new JsonPackIndexStore(source.Root);
        await new PrivateBackupService(
            source.Database,
            source.Settings,
            packages,
            new RuntimeDatabaseSnapshotService(source.Database),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            "1.0.0").CreateAsync(backupPath, CancellationToken.None);
    }

    private static void ReplaceSettingsMember(string backupPath, string settingsJson)
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update);
        var manifestEntry = archive.GetEntry(BackupFormat.ManifestMember)!;
        PrivateBackupManifest manifest;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
        {
            manifest = JsonSerializer.Deserialize<PrivateBackupManifest>(reader.ReadToEnd())!;
        }

        manifestEntry.Delete();
        archive.GetEntry(BackupFormat.SettingsMember)!.Delete();
        var settingsBytes = Encoding.UTF8.GetBytes(settingsJson);
        var settingsEntry = archive.CreateEntry(BackupFormat.SettingsMember, CompressionLevel.NoCompression);
        using (var stream = settingsEntry.Open())
        {
            stream.Write(settingsBytes);
        }

        var updatedManifest = new PrivateBackupManifest(
            manifest.FormatVersion,
            manifest.ApplicationVersion,
            manifest.DatabaseSchemaVersion,
            manifest.CreatedAtUtc,
            manifest.Files.Select(member => member.Path == BackupFormat.SettingsMember
                ? new BackupMember(
                    member.Path,
                    settingsBytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(settingsBytes)).ToLowerInvariant())
                : member).ToArray());
        var updatedManifestEntry = archive.CreateEntry(BackupFormat.ManifestMember, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(updatedManifestEntry.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(updatedManifest));
    }

    private PrivateBackupRestoreService CreateRestoreService(
        State current,
        FakeAgentRuntime runtime,
        IBackupStateSwapper? swapper = null,
        IApplicationSettingsDocument? settingsDocument = null,
        IAppMaintenanceCoordinator? maintenance = null,
        Action<string>? safeLog = null)
    {
        settingsDocument ??= current.Settings;
        var packages = new JsonPackIndexStore(current.Root);
        var rollbackBackup = new PrivateBackupService(
            current.Database,
            settingsDocument,
            packages,
            new RuntimeDatabaseSnapshotService(current.Database),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            "1.0.0");
        return new PrivateBackupRestoreService(
            current.Database,
            settingsDocument,
            packages,
            rollbackBackup,
            new PrivateBackupReader(),
            maintenance ?? new AppMaintenanceCoordinator(runtime),
            current.Root,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            swapper,
            safeLog: safeLog);
    }

    private sealed class RollbackBlockingSwap : IBackupStateSwapper
    {
        public Task SwapAsync(BackupStateSwapContext context, CancellationToken cancellationToken)
        {
            File.Move(context.StagedDatabasePath, context.CurrentDatabasePath, overwrite: true);
            File.Delete(context.CurrentSettingsPath);
            Directory.CreateDirectory(context.CurrentSettingsPath);
            throw new IOException("synthetic partial replacement and rollback failure");
        }
    }

    private static State CreateState(
        string root,
        string rowId,
        string userName,
        AgentExecutionStatus status,
        string? remoteTaskId = null)
    {
        var database = TestRuntimeDatabase.Create(Path.Combine(root, "runtime.db"));
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

        var settings = new ApplicationSettingsCoordinator(new JsonSettingsDocumentStore(root));
        ((ICharacterSettingsStore)settings).Save(CharacterSettings.Defaults with
        {
            UserProfile = new UserProfile(userName),
        });
        ((IWorkExecutionSettingsStore)settings).Save(WorkExecutionSettings.Defaults with
        {
            AgentConnection = new AgentConnectionSettings(Enabled: status == AgentExecutionStatus.Active),
        });
        new JsonPackIndexStore(root).Save(PackIndexV1.Empty);
        SqliteConnection.ClearAllPools();
        return new State(root, database, settings);
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

    private sealed record State(string Root, RuntimeDatabase Database, ApplicationSettingsCoordinator Settings);

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

    private sealed class ObservingApplicationSettingsDocument(IApplicationSettingsDocument inner)
        : IApplicationSettingsDocument
    {
        public string Location => inner.Location;
        public int ValidateCount { get; private set; }
        public string Export() => inner.Export();

        public SettingsRestoreMetadata ValidateForRestore(string document)
        {
            ValidateCount++;
            return inner.ValidateForRestore(document);
        }
    }
}
