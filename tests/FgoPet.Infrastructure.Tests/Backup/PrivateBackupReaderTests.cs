using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Backup;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Settings;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Backup;

public sealed class PrivateBackupReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fgo-backup-reader-{Guid.NewGuid():N}");
    private readonly string _archivePath;
    private readonly string _stagingPath;

    public PrivateBackupReaderTests()
    {
        _archivePath = Path.Combine(_root, "state.fgopetbackup");
        _stagingPath = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Valid_archive_is_extracted_and_staged_database_is_migrated_and_checked()
    {
        var sourceDatabasePath = Path.Combine(_root, "source.db");
        var sourceDatabase = new RuntimeDatabase(sourceDatabasePath);
        new RuntimeDatabaseMigrator(sourceDatabase).Migrate();
        var snapshotPath = Path.Combine(_root, "runtime.sqlite");
        await new RuntimeDatabaseSnapshotService(sourceDatabase).CreateAsync(snapshotPath, CancellationToken.None);

        var settingsJson = new AppSettingsSnapshotCodec().Serialize(AppSettings.Defaults);
        var packagesJson = "{\"schema_version\":1,\"selected\":null,\"last_known_good\":null}";
        WriteArchive(_archivePath, snapshotPath, settingsJson, packagesJson, databaseSchemaVersion: 8);

        var result = await new PrivateBackupReader().ReadAndValidateAsync(_archivePath, _stagingPath, CancellationToken.None);

        Assert.Equal(_stagingPath, result.StagingDirectory);
        Assert.True(File.Exists(result.RuntimeDatabasePath));
        Assert.Equal(8L, ReadScalar<long>(result.RuntimeDatabasePath, "SELECT MAX(version) FROM schema_migrations"));
        Assert.Equal("ok", ReadScalar<string>(result.RuntimeDatabasePath, "PRAGMA integrity_check"));
        Assert.Equal(AppSettings.Defaults, new AppSettingsSnapshotCodec().Deserialize(File.ReadAllText(result.SettingsPath)));
        Assert.Contains("schema_version", File.ReadAllText(result.PackagesPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.json", BackupFailureCode.UnsafePath)]
    [InlineData("extra.json", BackupFailureCode.UnexpectedMember)]
    public async Task Rejects_unsafe_or_unknown_entries_before_extracting(string entryName, BackupFailureCode code)
    {
        using (var archive = ZipFile.Open(_archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("not trusted");
        }

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            new PrivateBackupReader().ReadAndValidateAsync(_archivePath, _stagingPath, CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.False(Directory.Exists(_stagingPath));
        Assert.False(File.Exists(Path.Combine(_root, "escape.json")));
    }

    [Fact]
    public async Task Rejects_duplicate_members_and_hash_or_length_mismatch()
    {
        var sourceDatabasePath = Path.Combine(_root, "source.db");
        var sourceDatabase = new RuntimeDatabase(sourceDatabasePath);
        new RuntimeDatabaseMigrator(sourceDatabase).Migrate();
        var snapshotPath = Path.Combine(_root, "runtime.sqlite");
        await new RuntimeDatabaseSnapshotService(sourceDatabase).CreateAsync(snapshotPath, CancellationToken.None);
        var settingsJson = new AppSettingsSnapshotCodec().Serialize(AppSettings.Defaults);
        var packagesJson = "{\"schema_version\":1,\"selected\":null,\"last_known_good\":null}";

        WriteArchive(_archivePath, snapshotPath, settingsJson, packagesJson, databaseSchemaVersion: 8, duplicateSettings: true);

        var error = await Assert.ThrowsAsync<BackupException>(() =>
            new PrivateBackupReader().ReadAndValidateAsync(_archivePath, _stagingPath, CancellationToken.None));

        Assert.Equal(BackupFailureCode.DuplicateMember, error.Code);
        Assert.False(Directory.Exists(_stagingPath));
    }

    [Fact]
    public async Task Rejects_future_database_schema_and_invalid_settings_or_package_references()
    {
        var sourceDatabasePath = Path.Combine(_root, "source.db");
        var sourceDatabase = new RuntimeDatabase(sourceDatabasePath);
        new RuntimeDatabaseMigrator(sourceDatabase).Migrate();
        var snapshotPath = Path.Combine(_root, "runtime.sqlite");
        await new RuntimeDatabaseSnapshotService(sourceDatabase).CreateAsync(snapshotPath, CancellationToken.None);
        var validSettings = new AppSettingsSnapshotCodec().Serialize(AppSettings.Defaults);
        var validPackages = "{\"schema_version\":1,\"selected\":null,\"last_known_good\":null}";

        WriteArchive(_archivePath, snapshotPath, validSettings, validPackages, databaseSchemaVersion: 99);
        var futureError = await Assert.ThrowsAsync<BackupException>(() =>
            new PrivateBackupReader().ReadAndValidateAsync(_archivePath, _stagingPath, CancellationToken.None));
        Assert.Equal(BackupFailureCode.DatabaseVersionUnsupported, futureError.Code);

        WriteArchive(_archivePath, snapshotPath, "{bad", validPackages, databaseSchemaVersion: 8);
        var settingsError = await Assert.ThrowsAsync<BackupException>(() =>
            new PrivateBackupReader().ReadAndValidateAsync(_archivePath, _stagingPath + "-settings", CancellationToken.None));
        Assert.Equal(BackupFailureCode.SettingsInvalid, settingsError.Code);

        WriteArchive(_archivePath, snapshotPath, validSettings, "{\"schema_version\":99}", databaseSchemaVersion: 8);
        var packageError = await Assert.ThrowsAsync<BackupException>(() =>
            new PrivateBackupReader().ReadAndValidateAsync(_archivePath, _stagingPath + "-packages", CancellationToken.None));
        Assert.Equal(BackupFailureCode.PackageReferencesInvalid, packageError.Code);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteArchive(
        string archivePath,
        string snapshotPath,
        string settingsJson,
        string packagesJson,
        long databaseSchemaVersion,
        bool duplicateSettings = false)
    {
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [BackupFormat.RuntimeDatabaseMember] = File.ReadAllBytes(snapshotPath),
            [BackupFormat.SettingsMember] = Encoding.UTF8.GetBytes(settingsJson),
            [BackupFormat.PackagesMember] = Encoding.UTF8.GetBytes(packagesJson),
        };
        var manifest = new PrivateBackupManifest(
            BackupFormat.CurrentVersion,
            "1.0.0",
            databaseSchemaVersion,
            DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
            BackupFormat.PayloadMembers.Select(name => new BackupMember(
                name,
                payload[name].LongLength,
                Convert.ToHexString(SHA256.HashData(payload[name])).ToLowerInvariant())).ToArray());
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteEntry(archive, BackupFormat.ManifestMember, JsonSerializer.Serialize(manifest));
        foreach (var member in BackupFormat.PayloadMembers)
        {
            WriteEntry(archive, member, payload[member]);
        }

        if (duplicateSettings)
        {
            WriteEntry(archive, BackupFormat.SettingsMember, payload[BackupFormat.SettingsMember]);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content) =>
        WriteEntry(archive, name, Encoding.UTF8.GetBytes(content));

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static T ReadScalar<T>(string databasePath, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
