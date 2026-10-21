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
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;
using FgoPet.SettingsHost;
using FgoPet.Tests.Diagnostics;
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
        Action<string>? safeLog = null,
        IEnumerable<IDerivedConversationData>? derivedData = null)
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
            safeLog: safeLog, derivedData: derivedData);
    }

    [Fact]
    public async Task Restore_resets_understanding_grants_and_merges_current_deletion_tombstones_in_staging()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var root = new ConversationSourceSnapshot(new("c", "mash", null), "u", new('A', 64), DateTimeOffset.UtcNow, ConversationSourceKind.User, null);
        var scope = new ConversationScope("mash", null);
        var sourceOwner = new UnderstandingService(source.Database, new UnderstandingSources());
        var currentOwner = new UnderstandingService(current.Database, new UnderstandingSources());
        foreach (var owner in new[] { sourceOwner, currentOwner })
        {
            owner.Configure(scope, 0, new(UnderstandingMode.Proposals, ReadSources: true, SaveCandidates: true, UseInDialogue: true));
            owner.Apply(owner.Begin(scope, "batch", [root]), [new("deleted-derived", UnderstandingDomain.User, UnderstandingKind.Interpretation,
                "user", "synthetic derived", "only fixture", [root])]);
        }
        currentOwner.Review(scope, "delete", "deleted-derived", 1, UnderstandingAction.Delete);
        var path = Path.Combine(_root, "understanding.fgopetbackup"); await CreateBackupAsync(source, path);
        var runtime = new FakeAgentRuntime();
        var restored = await CreateRestoreService(current, runtime, derivedData: [currentOwner]).RestoreAsync(path, default);
        Assert.Equal(BackupRestoreStatus.Restored, restored.Status); Assert.Equal(0, runtime.DispatchCount);
        var reopened = new UnderstandingService(current.Database, new UnderstandingSources());
        Assert.Empty(reopened.List(scope, true)); Assert.Empty(reopened.List(scope, false));
        Assert.Equal(new UnderstandingPolicy(), reopened.State(scope).Policy);
    }
    [Theory]
    [InlineData("normalizer-fails")]
    [InlineData("cancel-before-swap")]
    [InlineData("swap-fails")]
    [InlineData("cancel-after-swap")]
    [InlineData("rollback-fails")]
    [InlineData("move-swap-fails")]
    [InlineData("cancel-after-atomic-move")]
    public async Task Understanding_participant_preserves_live_state_or_recovery_boundary_on_restore_failure(string fault)
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        var source = CreateState(_sourceRoot, "source-row", "source-user", AgentExecutionStatus.Completed);
        var scope = new ConversationScope("mash", null);
        UnderstandingService Seed(State state, string id)
        {
            var owner = new UnderstandingService(state.Database, new UnderstandingSources());
            owner.Configure(scope, 0, new(UnderstandingMode.Proposals, ReadSources: true, SaveCandidates: true, UseInDialogue: true));
            var root = new ConversationSourceSnapshot(new("c", "mash", null), "u", new('A', 64), DateTimeOffset.UtcNow, ConversationSourceKind.User, null);
            owner.Apply(owner.Begin(scope, "batch", [root]), [new(id, UnderstandingDomain.User, UnderstandingKind.Interpretation,
                "user", "synthetic-" + id, "only this fixture", [root])]);
            owner.Review(scope, "approve", id, 1, UnderstandingAction.Approve);
            return owner;
        }
        var currentOwner = Seed(current, "current-derived"); Seed(source, "backup-derived");
        var expectedState = currentOwner.State(scope);
        var settingsBefore = File.ReadAllBytes(current.Settings.Location);
        var path = Path.Combine(_root, "participant-fault.fgopetbackup"); await CreateBackupAsync(source, path);
        using var cts = new CancellationTokenSource();
        string? normalizedDirectory = null;
        var probe = new UnderstandingRestoreProbe(currentOwner, staging =>
        {
            normalizedDirectory = Path.GetDirectoryName(staging);
            Assert.NotEqual(Path.GetFullPath(current.Database.DatabasePath), Path.GetFullPath(staging));
            var staged = new UnderstandingService(new RuntimeDatabase(staging, pooling: false), new UnderstandingSources());
            Assert.Equal(new UnderstandingPolicy(), staged.State(scope).Policy);
            if (fault == "normalizer-fails") throw new IOException("synthetic participant failure");
            if (fault == "cancel-before-swap") cts.Cancel();
        });
        IBackupStateSwapper? swapper = fault switch
        {
            "swap-fails" => new PartiallyFailingSwap(),
            "cancel-after-swap" => new CancelAfterDatabaseSwap(cts),
            "rollback-fails" => new RollbackBlockingSwap(),
            "move-swap-fails" => new MoveDatabaseThenFail(),
            "cancel-after-atomic-move" => new CancelAfterAtomicMove(cts),
            _ => null,
        };
        var runtime = new FakeAgentRuntime();
        var restore = CreateRestoreService(current, runtime, swapper, derivedData: [probe]);
        if (fault == "cancel-before-swap")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore.RestoreAsync(path, cts.Token));
        else
        {
            var result = await restore.RestoreAsync(path, cts.Token);
            var expected = fault == "normalizer-fails" ? BackupRestoreStatus.Rejected :
                fault == "rollback-fails" ? BackupRestoreStatus.RecoveryRequired : BackupRestoreStatus.RolledBack;
            Assert.Equal(expected, result.Status);
        }
        Assert.Equal(1, probe.Calls); Assert.Equal(0, runtime.DispatchCount);
        if (fault == "rollback-fails")
        {
            Assert.True(File.Exists(PrivateBackupRestoreService.RecoveryMarkerPath(current.Root)));
            Assert.Single(Directory.GetDirectories(current.Root, "*.rollback"));
            Assert.Equal(BackupRestoreStatus.RecoveryRequired, (await restore.RestoreAsync(path, default)).Status);
            Assert.Equal(1, probe.Calls);
        }
        else
        {
            var reopened = new UnderstandingService(current.Database, new UnderstandingSources());
            Assert.Equal(expectedState, reopened.State(scope));
            Assert.Equal("current-derived", Assert.Single(reopened.Query(scope, "").Items).Id);
            Assert.Equal(settingsBefore, File.ReadAllBytes(current.Settings.Location));
            Assert.False(File.Exists(PrivateBackupRestoreService.RecoveryMarkerPath(current.Root)));
            var remainingStaging = Directory.GetDirectories(current.Root, "*.staging");
            if (remainingStaging.Length != 0)
                Console.WriteLine("H1_STAGING_METADATA_ONLY " + JsonSerializer.Serialize(new
                {
                    fault, directoryCount = remainingStaging.Length,
                    directoryKinds = remainingStaging.Select(p => Path.GetFileName(p).StartsWith(".restore-", StringComparison.Ordinal) ? "restore" : "other").ToArray(),
                    normalizedRestoreStageRemains = remainingStaging.Any(p => string.Equals(p, normalizedDirectory, StringComparison.OrdinalIgnoreCase)),
                    rollbackBackupStageRemains = remainingStaging.Any(p => Path.GetFileName(p).StartsWith(".restore-rollback.fgopetbackup.", StringComparison.Ordinal)),
                    files = remainingStaging.SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        .Select(p => new { extension = Path.GetExtension(p), bytes = new FileInfo(p).Length,
                            attributes = File.GetAttributes(p).ToString(), exclusiveRead = CanReadExclusively(p) }).ToArray(),
                }));
            Assert.Empty(remainingStaging);
        }
    }

    [Fact] public async Task H1_backup_staging_cleanup_control_without_understanding()
    {
        var current = CreateState(_currentRoot, "old-row", "old-user", AgentExecutionStatus.Completed);
        await CreateBackupAsync(current, Path.Combine(current.Root, "restore-rollback.fgopetbackup"));
        var directories = Directory.GetDirectories(current.Root, ".restore-rollback.fgopetbackup.*.staging");
        var files = directories.SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)).ToArray();
        var lockedBeforePoolClear = files.Count(p => !CanReadExclusively(p));
        SqliteConnection.ClearAllPools();
        Console.WriteLine("H1_BACKUP_BASELINE_METADATA_ONLY " + JsonSerializer.Serialize(new
        {
            understandingParticipants = 0, directoryCount = directories.Length, fileCount = files.Length,
            lockedBeforePoolClear, lockedAfterPoolClear = files.Count(p => !CanReadExclusively(p)),
        }));
        Assert.Empty(directories);
    }

    private static bool CanReadExclusively(string path)
    {
        try { using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed class UnderstandingRestoreProbe(UnderstandingService owner, Action<string> afterNormalize) : IDerivedConversationData
    {
        public int Calls;
        public void NormalizeRestoredCopy(string path) { Calls++; owner.NormalizeRestoredCopy(path); afterNormalize(path); }
        public void ForgetConversation(string conversationId, string roleId) => owner.ForgetConversation(conversationId, roleId);
        public void ForgetAll() => owner.ForgetAll();
        public JsonElement Export() => owner.Export();
    }
    private sealed class CancelAfterDatabaseSwap(CancellationTokenSource cancellation) : IBackupStateSwapper
    {
        public Task SwapAsync(BackupStateSwapContext context, CancellationToken token)
        {
            File.Copy(context.StagedDatabasePath, context.CurrentDatabasePath, overwrite: true);
            cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
    }
    private sealed class MoveDatabaseThenFail : IBackupStateSwapper
    {
        public Task SwapAsync(BackupStateSwapContext context, CancellationToken token)
        {
            File.Move(context.StagedDatabasePath, context.CurrentDatabasePath, overwrite: true);
            throw new IOException("synthetic failure after production-style database move");
        }
    }
    private sealed class CancelAfterAtomicMove(CancellationTokenSource cancellation) : IBackupStateSwapper
    {
        public async Task SwapAsync(BackupStateSwapContext context, CancellationToken token)
        {
            await new AtomicBackupStateSwapper().SwapAsync(context, token);
            cancellation.Cancel(); token.ThrowIfCancellationRequested();
        }
    }
    private sealed class UnderstandingSources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
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
        var settingsPath = Path.Combine(root, "settings.json");
        SyntheticFileOperationDiagnostics.Record("FGO_PET_PRIVATE_RESTORE_DIAGNOSTICS", "initial_settings_save", "before", settingsPath + ".tmp", settingsPath);
        try
        {
            ((ICharacterSettingsStore)settings).Save(CharacterSettings.Defaults with
            {
                UserProfile = new UserProfile(userName),
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SyntheticFileOperationDiagnostics.Record("FGO_PET_PRIVATE_RESTORE_DIAGNOSTICS", "initial_settings_save", "failed", settingsPath + ".tmp", settingsPath, error);
            throw;
        }
        SyntheticFileOperationDiagnostics.Record("FGO_PET_PRIVATE_RESTORE_DIAGNOSTICS", "initial_settings_save", "completed", settingsPath + ".tmp", settingsPath);
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
