using System;
using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Agents;
using FgoPet.Core.Backup;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.App.Privacy;
using FgoPet.Infrastructure.Backup;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.App.Tests.Privacy;

public sealed class PrivateBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fgo-private-backup-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly string _backupPath;

    public PrivateBackupServiceTests()
    {
        _databasePath = Path.Combine(_root, "runtime.db");
        _backupPath = Path.Combine(_root, "out", "state.fgopetbackup");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Creates_exact_four_members_with_safe_settings_package_refs_and_s2_remote_id()
    {
        var database = CreateDatabase();
        using (var connection = database.Open())
        {
            Execute(connection, "INSERT INTO focus_presets VALUES('short','builtin',300,60,1,'2026-09-02T00:00:00Z')");
            Execute(connection, "INSERT INTO agent_executions(execution_id, todo_id, source_type, source_instance, task_id, dispatch_request_id, status, updated_at_utc, remote_task_id) VALUES('execution-1','todo-1','codex','instance-1','task-1','dispatch-1','active','2026-09-02T00:00:00Z','remote-1')");
        }

        var settings = new FakeSettingsStore(AppSettings.Defaults with
        {
            Selection = new PortraitSelection("official.mash", "casual", "1.0.0"),
            ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
            UserProfile = new UserProfile("xqj"),
            AgentConnection = new AgentConnectionSettings(
                Enabled: true,
                SourceEnabled: new Dictionary<string, bool> { ["codex"] = true },
                ProjectAllowlist: new Dictionary<string, IReadOnlyList<AgentProjectTarget>>
                {
                    ["codex"] = new[] { new AgentProjectTarget("project-1", "Project") },
                }),
        });
        var packages = new FakePackIndexStore(new PackIndexV1(
            new PortraitSelection("official.mash", "casual", "1.0.0"),
            new PortraitSelection("official.mash", "default", "1.0.0")));

        var service = new PrivateBackupService(
            database,
            settings,
            packages,
            new RuntimeDatabaseSnapshotService(database),
            new AppSettingsSnapshotCodec(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")),
            "1.0.0");

        await service.CreateAsync(_backupPath, CancellationToken.None);

        using var archive = ZipFile.OpenRead(_backupPath);
        Assert.Equal(BackupFormat.RequiredMembers, archive.Entries.Select(entry => entry.FullName).ToArray());
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("-wal", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("-shm", StringComparison.Ordinal));

        var manifest = JsonSerializer.Deserialize<PrivateBackupManifest>(ReadEntry(archive, BackupFormat.ManifestMember));
        Assert.NotNull(manifest);
        Assert.Equal(BackupFormat.CurrentVersion, manifest!.FormatVersion);
        Assert.Equal(BackupFormat.PayloadMembers, manifest.Files.Select(file => file.Path).ToArray());
        foreach (var file in manifest.Files)
        {
            var bytes = ReadEntryBytes(archive, file.Path);
            Assert.Equal(bytes.Length, file.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Sha256);
        }

        var settingsJson = ReadEntry(archive, BackupFormat.SettingsMember);
        Assert.Contains("official.mash", settingsJson, StringComparison.Ordinal);
        Assert.Contains("gpt-4o-mini", settingsJson, StringComparison.Ordinal);
        Assert.Contains("project-1", settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", settingsJson, StringComparison.OrdinalIgnoreCase);

        var packagesJson = ReadEntry(archive, BackupFormat.PackagesMember);
        Assert.Contains("official.mash", packagesJson, StringComparison.Ordinal);
        Assert.Contains("casual", packagesJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(_root), packagesJson, StringComparison.OrdinalIgnoreCase);

        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExtractSnapshot(archive),
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        snapshot.Open();
        Assert.Equal("remote-1", Scalar<string>(snapshot, "SELECT remote_task_id FROM agent_executions WHERE execution_id='execution-1'"));
    }

    [Fact]
    public async Task Repeated_creation_with_fixed_time_is_byte_stable_and_replaces_destination_atomically()
    {
        var database = CreateDatabase();
        var settings = new FakeSettingsStore(AppSettings.Defaults);
        var packages = new FakePackIndexStore(PackIndexV1.Empty);
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z"));
        var service = new PrivateBackupService(database, settings, packages, new RuntimeDatabaseSnapshotService(database), new AppSettingsSnapshotCodec(), clock, "1.0.0");
        var firstPath = Path.Combine(_root, "first.fgopetbackup");
        await service.CreateAsync(firstPath, CancellationToken.None);
        var firstBytes = File.ReadAllBytes(firstPath);

        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.WriteAllText(_backupPath, "old destination", Encoding.UTF8);
        await service.CreateAsync(_backupPath, CancellationToken.None);
        var secondBytes = File.ReadAllBytes(_backupPath);

        Assert.Equal(firstBytes, secondBytes);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(_backupPath)!), path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_leaves_an_existing_destination_untouched()
    {
        var database = CreateDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.WriteAllText(_backupPath, "old destination", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var service = new PrivateBackupService(database, new FakeSettingsStore(AppSettings.Defaults), new FakePackIndexStore(PackIndexV1.Empty), new RuntimeDatabaseSnapshotService(database), new AppSettingsSnapshotCodec(), TimeProvider.System, "1.0.0");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(_backupPath, cancellation.Token));

        Assert.Equal("old destination", File.ReadAllText(_backupPath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_databasePath);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
    }

    private static string ReadEntry(ZipArchive archive, string name) =>
        Encoding.UTF8.GetString(ReadEntryBytes(archive, name));

    private static byte[] ReadEntryBytes(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private string ExtractSnapshot(ZipArchive archive)
    {
        var path = Path.Combine(_root, "snapshot-read.db");
        File.WriteAllBytes(path, ReadEntryBytes(archive, BackupFormat.RuntimeDatabaseMember));
        return path;
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

    private sealed class FakeSettingsStore(AppSettings current) : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => current;
        public void Save(AppSettings settings) => current = settings;
    }

    private sealed class FakePackIndexStore(PackIndexV1 current) : IPackIndexStore
    {
        public string Location => "memory";
        public PackIndexV1 Load() => current;
        public void Save(PackIndexV1 index) => current = index;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
