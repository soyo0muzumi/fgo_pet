using System;
using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Backup;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;

namespace FgoPet.App.Privacy;

/// <summary>
/// Creates the private, restorable backup format. This path deliberately does
/// not share the user-facing export document or include credentials/package data.
/// </summary>
public sealed class PrivateBackupService
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly IAppSettingsStore _settings;
    private readonly IPackIndexStore _packages;
    private readonly RuntimeDatabaseSnapshotService _snapshotService;
    private readonly AppSettingsSnapshotCodec _settingsCodec;
    private readonly BackupPackageReferencesCodec _packageCodec;
    private readonly TimeProvider _clock;
    private readonly string _applicationVersion;
    private readonly Action<string>? _safeLog;

    public PrivateBackupService(
        RuntimeDatabase database,
        IAppSettingsStore settings,
        IPackIndexStore packages,
        RuntimeDatabaseSnapshotService snapshotService,
        AppSettingsSnapshotCodec settingsCodec,
        TimeProvider clock,
        string applicationVersion,
        Action<string>? safeLog = null,
        BackupPackageReferencesCodec? packageCodec = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _settingsCodec = settingsCodec ?? throw new ArgumentNullException(nameof(settingsCodec));
        _packageCodec = packageCodec ?? new BackupPackageReferencesCodec();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _applicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
            ? throw new ArgumentException("Application version is required.", nameof(applicationVersion))
            : applicationVersion;
        _safeLog = safeLog;
    }

    public async Task CreateAsync(string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var destination = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            throw new BackupException(BackupFailureCode.SwapFailed, "The backup destination directory is invalid.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var stagingDirectory = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingDirectory);

        var snapshotPath = Path.Combine(stagingDirectory, BackupFormat.RuntimeDatabaseMember);
        var settingsPath = Path.Combine(stagingDirectory, BackupFormat.SettingsMember);
        var packagesPath = Path.Combine(stagingDirectory, BackupFormat.PackagesMember);
        var temporaryArchivePath = Path.Combine(stagingDirectory, "archive.tmp");
        var moved = false;

        try
        {
            _safeLog?.Invoke("backup.assembly.started");
            await _snapshotService.CreateAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var settingsJson = _settingsCodec.Serialize(_settings.Load());
            var packageIndex = _packages.Load();
            var packagesJson = _packageCodec.Serialize(new BackupPackageReferences(
                packageIndex.Selected,
                packageIndex.LastKnownGood));
            WriteUtf8(settingsPath, settingsJson, cancellationToken);
            WriteUtf8(packagesPath, packagesJson, cancellationToken);

            var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [BackupFormat.RuntimeDatabaseMember] = await ReadBytesAsync(snapshotPath, cancellationToken).ConfigureAwait(false),
                [BackupFormat.SettingsMember] = Utf8.GetBytes(settingsJson),
                [BackupFormat.PackagesMember] = Utf8.GetBytes(packagesJson),
            };
            var members = payload.Select(pair => new BackupMember(
                pair.Key,
                pair.Value.LongLength,
                Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant())).ToArray();
            var manifest = new PrivateBackupManifest(
                BackupFormat.CurrentVersion,
                _applicationVersion,
                ReadSnapshotSchemaVersion(payload[BackupFormat.RuntimeDatabaseMember]),
                _clock.GetUtcNow(),
                members);
            BackupArchivePolicy.ValidateManifest(manifest);

            using (var archive = ZipFile.Open(temporaryArchivePath, ZipArchiveMode.Create))
            {
                foreach (var member in BackupFormat.RequiredMembers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = member == BackupFormat.ManifestMember
                        ? Utf8.GetBytes(JsonSerializer.Serialize(manifest))
                        : payload[member];
                    var entry = archive.CreateEntry(member, CompressionLevel.NoCompression);
                    entry.LastWriteTime = manifest.CreatedAtUtc;
                    using var stream = entry.Open();
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryArchivePath, destination, overwrite: true);
                moved = true;
            }
            catch (IOException exception)
            {
                throw new BackupException(
                    BackupFailureCode.SwapFailed,
                    "The completed backup could not replace the requested destination.",
                    exception);
            }

            _safeLog?.Invoke("backup.archive.created");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BackupException(
                BackupFailureCode.DatabaseInvalid,
                "The private backup could not be created.",
                exception);
        }
        finally
        {
            if (!moved && File.Exists(temporaryArchivePath))
            {
                TryDelete(temporaryArchivePath);
            }

            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void WriteUtf8(string path, string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllBytes(path, Utf8.GetBytes(content));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static long ReadSnapshotSchemaVersion(byte[] snapshot)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"fgo-backup-schema-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(tempPath, snapshot);
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
            connection.Open();
            return RuntimeDatabaseMigrator.ReadVersion(connection);
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BackupException(
                BackupFailureCode.DatabaseInvalid,
                "The private backup database schema could not be read.",
                exception);
        }
        finally
        {
            TryDelete(tempPath);
            TryDelete(tempPath + "-wal");
            TryDelete(tempPath + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup; the original operation result remains authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup; the original operation result remains authoritative.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup; the original operation result remains authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup; the original operation result remains authoritative.
        }
    }

}
