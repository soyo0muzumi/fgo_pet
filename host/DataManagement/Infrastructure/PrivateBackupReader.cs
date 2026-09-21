using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Backup;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Backup;

public sealed record ValidatedPrivateBackup(
    string StagingDirectory,
    string RuntimeDatabasePath,
    string SettingsPath,
    string PackagesPath,
    PrivateBackupManifest Manifest,
    BackupPackageReferences PackageReferences);

/// <summary>
/// Validates and extracts a private backup into an isolated staging directory.
/// No current application state is opened or modified by this reader.
/// </summary>
public sealed class PrivateBackupReader
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public async Task<ValidatedPrivateBackup> ReadAndValidateAsync(
        string backupPath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var archivePath = Path.GetFullPath(backupPath);
        var stagingPath = Path.GetFullPath(stagingDirectory);
        var stagingCreated = false;

        try
        {
            if (!File.Exists(archivePath))
            {
                throw new BackupException(BackupFailureCode.InvalidManifest, "The private backup file is unavailable.");
            }

            using var archive = ZipFile.OpenRead(archivePath);
            var entries = ScanEntries(archive);
            var manifestEntry = entries[BackupFormat.ManifestMember];
            var manifest = DeserializeManifest(await ReadEntryBytesAsync(manifestEntry, cancellationToken).ConfigureAwait(false));
            BackupArchivePolicy.ValidateManifest(manifest);

            if (Directory.Exists(stagingPath))
            {
                throw new BackupException(BackupFailureCode.UnsafePath, "The backup staging directory must be new.");
            }

            Directory.CreateDirectory(stagingPath);
            stagingCreated = true;
            var runtimePath = Path.Combine(stagingPath, BackupFormat.RuntimeDatabaseMember);
            var settingsPath = Path.Combine(stagingPath, BackupFormat.SettingsMember);
            var packagesPath = Path.Combine(stagingPath, BackupFormat.PackagesMember);

            foreach (var member in BackupFormat.PayloadMembers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifestMember = manifest.Files.Single(item => string.Equals(item.Path, member, StringComparison.Ordinal));
                var entry = entries[member];
                if (entry.Length != manifestMember.Length)
                {
                    throw new BackupException(BackupFailureCode.InvalidManifest, "Backup member length does not match its manifest.");
                }

                var destination = Path.Combine(stagingPath, member);
                await ExtractAndVerifyAsync(entry, manifestMember, destination, cancellationToken).ConfigureAwait(false);
            }

            ValidateSettings(settingsPath);
            var packageReferences = ValidatePackageReferences(packagesPath);
            ValidateAndMigrateDatabase(runtimePath, manifest.DatabaseSchemaVersion);

            return new ValidatedPrivateBackup(stagingPath, runtimePath, settingsPath, packagesPath, manifest, packageReferences);
        }
        catch (OperationCanceledException)
        {
            DeleteStaging(stagingPath, stagingCreated);
            throw;
        }
        catch (BackupException)
        {
            DeleteStaging(stagingPath, stagingCreated);
            throw;
        }
        catch (RuntimeDatabaseVersionException exception)
        {
            DeleteStaging(stagingPath, stagingCreated);
            throw new BackupException(
                BackupFailureCode.DatabaseVersionUnsupported,
                "The private backup database schema is newer than this application supports.",
                exception);
        }
        catch (JsonException exception)
        {
            DeleteStaging(stagingPath, stagingCreated);
            throw new BackupException(
                BackupFailureCode.InvalidManifest,
                "The private backup metadata is malformed.",
                exception);
        }
        catch (InvalidDataException exception)
        {
            DeleteStaging(stagingPath, stagingCreated);
            throw new BackupException(
                BackupFailureCode.InvalidManifest,
                "The private backup archive is malformed.",
                exception);
        }
        catch (Exception exception)
        {
            DeleteStaging(stagingPath, stagingCreated);
            throw new BackupException(
                BackupFailureCode.DatabaseInvalid,
                "The private backup could not be validated.",
                exception);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ScanEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            BackupArchivePolicy.ValidateMemberPath(entry.FullName);
            if (IsLinkOrDirectory(entry))
            {
                throw new BackupException(BackupFailureCode.UnsafePath, "Backup archive contains a non-file member.");
            }

            if (!BackupFormat.RequiredMembers.Contains(entry.FullName, StringComparer.Ordinal))
            {
                throw new BackupException(BackupFailureCode.UnexpectedMember, "Backup archive contains an unexpected member.");
            }

            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new BackupException(BackupFailureCode.DuplicateMember, "Backup archive contains duplicate members.");
            }

            if (entry.Length < 0 || entry.Length > BackupArchivePolicy.MaxMemberBytes)
            {
                throw new BackupException(BackupFailureCode.MemberTooLarge, "Backup archive member exceeds the size limit.");
            }

            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > BackupArchivePolicy.MaxArchiveBytes)
            {
                throw new BackupException(BackupFailureCode.ArchiveTooLarge, "Backup archive exceeds the size limit.");
            }
        }

        foreach (var required in BackupFormat.RequiredMembers)
        {
            if (!entries.ContainsKey(required))
            {
                throw new BackupException(BackupFailureCode.MissingMember, "Backup archive is missing a required member.");
            }
        }

        return entries;
    }

    private static bool IsLinkOrDirectory(ZipArchiveEntry entry)
    {
        if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixType is 0xA000 or 0x4000;
    }

    private static PrivateBackupManifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<PrivateBackupManifest>(bytes)
                ?? throw new BackupException(BackupFailureCode.InvalidManifest, "Backup manifest is empty.");
        }
        catch (BackupException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new BackupException(BackupFailureCode.InvalidManifest, "Backup manifest is malformed.", exception);
        }
    }

    private static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > BackupArchivePolicy.MaxMemberBytes)
            {
                throw new BackupException(BackupFailureCode.MemberTooLarge, "Backup archive member exceeds the size limit.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return memory.ToArray();
    }

    private static async Task ExtractAndVerifyAsync(
        ZipArchiveEntry entry,
        BackupMember manifestMember,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > BackupArchivePolicy.MaxMemberBytes)
            {
                throw new BackupException(BackupFailureCode.MemberTooLarge, "Backup archive member exceeds the size limit.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total != manifestMember.Length
            || !string.Equals(
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                manifestMember.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupException(BackupFailureCode.InvalidManifest, "Backup member integrity does not match its manifest.");
        }
    }

    private static void ValidateSettings(string path)
    {
        try
        {
            _ = new AppSettingsSnapshotCodec().Deserialize(File.ReadAllText(path, Utf8));
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            throw new BackupException(BackupFailureCode.SettingsInvalid, "Backup settings are invalid.", exception);
        }
    }

    private static BackupPackageReferences ValidatePackageReferences(string path)
    {
        try
        {
            return new BackupPackageReferencesCodec().Deserialize(File.ReadAllText(path, Utf8));
        }
        catch (BackupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            throw new BackupException(BackupFailureCode.PackageReferencesInvalid, "Backup package references are invalid.", exception);
        }
    }

    private static void ValidateAndMigrateDatabase(string path, long declaredVersion)
    {
        var actualVersion = ReadDatabaseVersion(path);
        if (actualVersion != declaredVersion)
        {
            var code = actualVersion > declaredVersion
                ? BackupFailureCode.InvalidManifest
                : BackupFailureCode.DatabaseVersionUnsupported;
            throw new BackupException(code, "Backup database schema metadata does not match the database.");
        }

        var staged = new RuntimeDatabase(path);
        new RuntimeDatabaseMigrator(staged).Migrate();
        long migratedVersion;
        using (var connection = OpenReadOnly(path))
        {
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupException(BackupFailureCode.DatabaseInvalid, "Backup database integrity validation failed.");
            }

            migratedVersion = RuntimeDatabaseMigrator.ReadVersion(connection);
        }

        if (migratedVersion != RuntimeDatabaseMigrator.CurrentSchemaVersion)
        {
            throw new BackupException(BackupFailureCode.DatabaseVersionUnsupported, "Backup database schema is unsupported.");
        }

    }

    private static long ReadDatabaseVersion(string path)
    {
        using var connection = OpenReadOnly(path);
        try
        {
            return RuntimeDatabaseMigrator.ReadVersion(connection);
        }
        catch (SqliteException exception)
        {
            throw new BackupException(BackupFailureCode.DatabaseInvalid, "Backup database schema could not be read.", exception);
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void DeleteStaging(string path, bool created)
    {
        if (!created)
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup; the original validation failure remains authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup; the original validation failure remains authoritative.
        }
    }

}
