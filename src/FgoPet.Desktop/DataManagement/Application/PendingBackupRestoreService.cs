using System.IO;
using System.Text.Json;
using FgoPet.Core.Backup;
using FgoPet.Infrastructure.Backup;
using FgoPet.SettingsHost;

namespace FgoPet.App.Privacy;

/// <summary>Queues validated private state for the next exclusive, pre-runtime startup.</summary>
public sealed class PendingBackupRestoreService
{
    private readonly string _root;
    private readonly IApplicationSettingsDocument _settings;
    private readonly PrivateBackupReader _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string PendingPath => Path.Combine(_root, ".pending-restore.fgopetbackup");
    private string ApplyingPath => Path.Combine(_root, ".applying-restore.fgopetbackup");

    public PendingBackupRestoreService(string storageRoot, IApplicationSettingsDocument settings, PrivateBackupReader reader)
    {
        _root = Path.GetFullPath(storageRoot);
        _settings = settings;
        _reader = reader;
    }

    public async Task PrepareAsync(string backupPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var preparation = Path.Combine(_root, $".restore-{Guid.NewGuid():N}.prepare");
        try
        {
            if (File.Exists(PendingPath) || File.Exists(ApplyingPath) || File.Exists(PrivateBackupRestoreService.RecoveryMarkerPath(_root)))
                throw new BackupException(BackupFailureCode.SwapFailed, "A restore or recovery is already pending.");
            Directory.CreateDirectory(preparation);
            var copy = Path.Combine(preparation, "backup.fgopetbackup");
            await using (var source = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length > BackupArchivePolicy.MaxArchiveBytes + 1024 * 1024)
                    throw new BackupException(BackupFailureCode.ArchiveTooLarge, "The private backup archive is too large.");
                await using var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            var validated = await _reader.ReadAndValidateAsync(copy, Path.Combine(preparation, "validated"), cancellationToken).ConfigureAwait(false);
            try { _settings.ValidateForRestore(await File.ReadAllTextAsync(validated.SettingsPath, cancellationToken).ConfigureAwait(false)); }
            catch (JsonException error) { throw new BackupException(BackupFailureCode.SettingsInvalid, "Backup settings are invalid.", error); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(copy, PendingPath); // No overwrite: a pending user decision is never replaced.
        }
        finally
        {
            try { if (Directory.Exists(preparation)) Directory.Delete(preparation, recursive: true); }
            finally { _gate.Release(); }
        }
    }

    public async Task<BackupRestoreResult?> ApplyBeforeStartupAsync(PrivateBackupRestoreService restore, CancellationToken cancellationToken)
        => await ApplyBeforeStartupAsync(() => restore, cancellationToken).ConfigureAwait(false);

    public async Task<BackupRestoreResult?> ApplyBeforeStartupAsync(Func<PrivateBackupRestoreService> createRestore, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(ApplyingPath) || File.Exists(PrivateBackupRestoreService.RecoveryMarkerPath(_root)))
                return new BackupRestoreResult(BackupRestoreStatus.RecoveryRequired, BackupFailureCode.SwapFailed, false, false);
            if (!File.Exists(PendingPath)) return null;
            File.Move(PendingPath, ApplyingPath);
            var result = await createRestore().RestoreAsync(ApplyingPath, cancellationToken).ConfigureAwait(false);
            // A crash or uncertain outcome retains the applying archive. Never
            // silently replay a previously interrupted destructive operation.
            if (result.Status != BackupRestoreStatus.RecoveryRequired) File.Delete(ApplyingPath);
            return result;
        }
        finally { _gate.Release(); }
    }
}
