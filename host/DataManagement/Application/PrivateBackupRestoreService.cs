using System.IO;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Backup;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace FgoPet.App.Privacy;

public sealed record BackupStateSwapContext(
    string StagedDatabasePath,
    string StagedSettingsPath,
    string CurrentDatabasePath,
    string CurrentSettingsPath);

public interface IBackupStateSwapper
{
    Task SwapAsync(BackupStateSwapContext context, CancellationToken cancellationToken);
}

/// <summary>Performs same-volume replacement of the database and settings files.</summary>
public sealed class AtomicBackupStateSwapper : IBackupStateSwapper
{
    public Task SwapAsync(BackupStateSwapContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceDatabase(context.StagedDatabasePath, context.CurrentDatabasePath);
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceFile(context.StagedSettingsPath, context.CurrentSettingsPath);
        return Task.CompletedTask;
    }

    private static void ReplaceDatabase(string staged, string current)
    {
        ReplaceFile(staged, current);
        ReplaceSidecar(staged + "-wal", current + "-wal");
        ReplaceSidecar(staged + "-shm", current + "-shm");
    }

    private static void ReplaceSidecar(string staged, string current)
    {
        TryDelete(current);
        if (File.Exists(staged))
        {
            File.Move(staged, current, overwrite: true);
        }
    }

    private static void ReplaceFile(string staged, string current)
    {
        var directory = Path.GetDirectoryName(current);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Move(staged, current, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Validates a private backup before acquiring the maintenance boundary and
/// atomically replaces the current state with rollback protection.
/// </summary>
public sealed class PrivateBackupRestoreService
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly RuntimeDatabase _currentDatabase;
    private readonly IAppSettingsStore _currentSettings;
    private readonly IPackIndexStore _currentPackages;
    private readonly PrivateBackupService _rollbackBackup;
    private readonly PrivateBackupReader _reader;
    private readonly IAppMaintenanceCoordinator _maintenance;
    private readonly string _storageRoot;
    private readonly TimeProvider _clock;
    private readonly IBackupStateSwapper _swapper;
    private readonly Func<CancellationToken, Task>? _startupSelfCheck;
    private readonly IArtPackageRepository? _packageRepository;
    private readonly AppSettingsSnapshotCodec _settingsCodec = new();
    private readonly BackupPackageReferencesCodec _packageCodec = new();
    private readonly Action<string>? _safeLog;

    public PrivateBackupRestoreService(
        RuntimeDatabase currentDatabase,
        IAppSettingsStore currentSettings,
        IPackIndexStore currentPackages,
        PrivateBackupService rollbackBackup,
        PrivateBackupReader reader,
        IAppMaintenanceCoordinator maintenance,
        string storageRoot,
        TimeProvider clock,
        IBackupStateSwapper? swapper = null,
        Func<CancellationToken, Task>? startupSelfCheck = null,
        IArtPackageRepository? packageRepository = null,
        Action<string>? safeLog = null)
    {
        _currentDatabase = currentDatabase ?? throw new ArgumentNullException(nameof(currentDatabase));
        _currentSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));
        _currentPackages = currentPackages ?? throw new ArgumentNullException(nameof(currentPackages));
        _rollbackBackup = rollbackBackup ?? throw new ArgumentNullException(nameof(rollbackBackup));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _storageRoot = Path.GetFullPath(storageRoot ?? throw new ArgumentNullException(nameof(storageRoot)));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _swapper = swapper ?? new AtomicBackupStateSwapper();
        _startupSelfCheck = startupSelfCheck;
        _packageRepository = packageRepository;
        _safeLog = safeLog;
    }

    public async Task<BackupRestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        cancellationToken.ThrowIfCancellationRequested();

        var stagingPath = Path.Combine(_storageRoot, $".restore-{Guid.NewGuid():N}.staging");
        ValidatedPrivateBackup validated;
        try
        {
            validated = await _reader.ReadAndValidateAsync(backupPath, stagingPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException exception)
        {
            return Rejected(exception.Code);
        }

        var rollbackDirectory = Path.Combine(_storageRoot, $".restore-{Guid.NewGuid():N}.rollback");
        var rollbackState = default(RollbackState);
        var swapStarted = false;
        try
        {
            var restoredSettings = _settingsCodec.Deserialize(File.ReadAllText(validated.SettingsPath, Utf8));
            await using var lease = await _maintenance.EnterAsync(cancellationToken).ConfigureAwait(false);
            _safeLog?.Invoke("restore.maintenance.entered");

            BackupDatabaseNormalizer.Normalize(new RuntimeDatabase(validated.RuntimeDatabasePath), _clock.GetUtcNow());
            SqliteConnection.ClearAllPools();

            var rollbackArchive = Path.Combine(_storageRoot, "restore-rollback.fgopetbackup");
            await _rollbackBackup.CreateAsync(rollbackArchive, cancellationToken).ConfigureAwait(false);
            rollbackState = CaptureCurrentState(rollbackDirectory);
            SqliteConnection.ClearAllPools();

            _safeLog?.Invoke("restore.swap.started");
            swapStarted = true;
            await _swapper.SwapAsync(new BackupStateSwapContext(
                validated.RuntimeDatabasePath,
                validated.SettingsPath,
                _currentDatabase.DatabasePath,
                _currentSettings.Location), cancellationToken).ConfigureAwait(false);
            _currentPackages.Save(new PackIndexV1(
                validated.PackageReferences.Selected,
                validated.PackageReferences.LastKnownGood));
            await RunStartupSelfCheckAsync(cancellationToken).ConfigureAwait(false);

            var missingPackage = await IsMissingPackageAsync(validated.PackageReferences.Selected, cancellationToken).ConfigureAwait(false);
            _safeLog?.Invoke("restore.completed");
            return new BackupRestoreResult(
                BackupRestoreStatus.Restored,
                FailureCode: null,
                PackageReinstallRequired: missingPackage,
                AgentPairingRequired: restoredSettings.AgentConnection.Enabled);
        }
        catch (OperationCanceledException)
        {
            if (swapStarted)
            {
                Rollback(rollbackState);
                return new BackupRestoreResult(BackupRestoreStatus.RolledBack, BackupFailureCode.SwapFailed, false, false);
            }

            throw;
        }
        catch (BackupException exception)
        {
            if (swapStarted)
            {
                Rollback(rollbackState);
                return new BackupRestoreResult(BackupRestoreStatus.RolledBack, exception.Code, false, false);
            }

            return Rejected(exception.Code);
        }
        catch (JsonException)
        {
            if (swapStarted)
            {
                Rollback(rollbackState);
                _safeLog?.Invoke("restore.rolled_back");
                return new BackupRestoreResult(BackupRestoreStatus.RolledBack, BackupFailureCode.SettingsInvalid, false, false);
            }

            return Rejected(BackupFailureCode.SettingsInvalid);
        }
        catch (Exception exception)
        {
            if (swapStarted)
            {
                Rollback(rollbackState);
                _safeLog?.Invoke("restore.rolled_back");
                return new BackupRestoreResult(BackupRestoreStatus.RolledBack, ClassifyFailure(exception), false, false);
            }

            return Rejected(ClassifyFailure(exception));
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
            TryDeleteDirectory(rollbackDirectory);
        }
    }

    private async Task RunStartupSelfCheckAsync(CancellationToken cancellationToken)
    {
        if (_startupSelfCheck is not null)
        {
            try
            {
                await _startupSelfCheck(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new BackupException(BackupFailureCode.StartupCheckFailed, "Restored state failed startup self-check.", exception);
            }

            return;
        }

        new RuntimeDatabaseMigrator(_currentDatabase).Migrate();
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _currentDatabase.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString()))
        {
            connection.Open();
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException(BackupFailureCode.StartupCheckFailed, "Restored database failed startup self-check.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = _settingsCodec.Deserialize(File.ReadAllText(_currentSettings.Location, Utf8));
        _ = _packageCodec.Deserialize(File.ReadAllText(_currentPackages.Location, Utf8));
    }

    private async Task<bool> IsMissingPackageAsync(PortraitSelection? selection, CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            return false;
        }

        if (_packageRepository is null)
        {
            return true;
        }

        return await _packageRepository.GetAppearanceAsync(selection, cancellationToken).ConfigureAwait(false) is null;
    }

    private RollbackState CaptureCurrentState(string rollbackDirectory)
    {
        Directory.CreateDirectory(rollbackDirectory);
        var files = new[]
        {
            _currentDatabase.DatabasePath,
            _currentDatabase.DatabasePath + "-wal",
            _currentDatabase.DatabasePath + "-shm",
            _currentSettings.Location,
            _currentPackages.Location,
        }.Select(path => new RollbackFile(path, Path.Combine(rollbackDirectory, $"{Guid.NewGuid():N}.state"))).ToArray();

        foreach (var file in files)
        {
            if (File.Exists(file.CurrentPath))
            {
                File.Copy(file.CurrentPath, file.RollbackPath, overwrite: false);
                file.Existed = true;
            }
        }

        return new RollbackState(files);
    }

    private void Rollback(RollbackState? state)
    {
        if (state is null)
        {
            return;
        }

        try
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in state.Files)
            {
                TryDelete(file.CurrentPath);
            }

            foreach (var file in state.Files.Where(file => file.Existed))
            {
                var directory = Path.GetDirectoryName(file.CurrentPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.Copy(file.RollbackPath, file.CurrentPath, overwrite: true);
            }
        }
        catch (IOException)
        {
            _safeLog?.Invoke("restore.rollback.cleanup_failed");
        }
        catch (UnauthorizedAccessException)
        {
            _safeLog?.Invoke("restore.rollback.cleanup_failed");
        }
    }

    private static BackupRestoreResult Rejected(BackupFailureCode code) =>
        new(BackupRestoreStatus.Rejected, code, false, false);

    private static BackupFailureCode ClassifyFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            ? BackupFailureCode.SwapFailed
            : BackupFailureCode.StartupCheckFailed;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RollbackFile(string currentPath, string rollbackPath)
    {
        public string CurrentPath { get; } = currentPath;
        public string RollbackPath { get; } = rollbackPath;
        public bool Existed { get; set; }
    }

    private sealed record RollbackState(IReadOnlyList<RollbackFile> Files);
}
