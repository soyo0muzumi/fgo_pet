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

namespace FgoPet.EndToEnd.Tests;

public sealed class BackupRestoreEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fgo-backup-e2e-{Guid.NewGuid():N}");
    private readonly string _sourceRoot;
    private readonly string _currentRoot;

    public BackupRestoreEndToEndTests()
    {
        _sourceRoot = Path.Combine(_root, "source");
        _currentRoot = Path.Combine(_root, "clean");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_currentRoot);
    }

    [Fact]
    public async Task Clean_directory_round_trip_restores_supported_business_state_and_safety_boundaries()
    {
        var source = CreateState(_sourceRoot, seedBusinessData: true);
        var current = CreateState(_currentRoot, seedBusinessData: false);
        var backupPath = Path.Combine(_root, "round-trip.fgopetbackup");
        await CreateBackupAsync(source, backupPath);

        var runtime = new FakeAgentRuntime();
        var result = await CreateRestoreService(current, runtime).RestoreAsync(backupPath, CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Restored, result.Status);
        Assert.True(result.PackageReinstallRequired);
        Assert.True(result.AgentPairingRequired);
        Assert.True(runtime.StopCalled);
        Assert.Equal(0, runtime.DispatchCount);

        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM focus_sessions WHERE session_id='focus-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM runtime_events WHERE event_id='event-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM timeline_entries WHERE entry_id='timeline-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM servant_bonds WHERE servant_id='mash_kyrielight'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM conversations WHERE conversation_id='conversation-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM chat_messages WHERE message_id='message-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM conversation_summaries WHERE summary_id='summary-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM memory_candidates WHERE candidate_id='candidate-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM memories WHERE memory_id='memory-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM todo_items WHERE todo_id='todo-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM agent_event_receipts WHERE task_id='task-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM work_archives WHERE archive_id='archive-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM work_archive_items WHERE archive_id='archive-1'"));
        Assert.Equal(1L, Scalar(current.Database.DatabasePath, "SELECT COUNT(*) FROM long_work_archives WHERE archive_id='long-1'"));

        var execution = new SqliteAgentRepository(current.Database).GetExecution("execution-1")!;
        Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, execution.Status);
        Assert.Equal("remote-task-1", execution.RemoteTaskId);
        Assert.Equal("xqj", new JsonAppSettingsStore(_currentRoot).Load().UserProfile!.DisplayName);
        var packageIndex = new JsonPackIndexStore(_currentRoot).Load();
        Assert.Equal(new PortraitSelection("official.mash", "casual", "1.0.0"), packageIndex.Selected);
        Assert.Equal(new PortraitSelection("official.mash", "default", "1.0.0"), packageIndex.LastKnownGood);

        using var archive = System.IO.Compression.ZipFile.OpenRead(backupPath);
        Assert.Equal(
            BackupFormat.RequiredMembers.OrderBy(name => name),
            archive.Entries.Select(entry => entry.FullName).OrderBy(name => name));
        foreach (var entryName in new[] { BackupFormat.ManifestMember, BackupFormat.SettingsMember, BackupFormat.PackagesMember })
        {
            using var reader = new StreamReader(archive.GetEntry(entryName)!.Open());
            var entryText = reader.ReadToEnd();
            Assert.DoesNotContain("api-key", entryText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(_sourceRoot), entryText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(_currentRoot), entryText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Corrupt_and_future_inputs_are_rejected_without_current_state_mutation()
    {
        var source = CreateState(_sourceRoot, seedBusinessData: true);
        var current = CreateState(_currentRoot, seedBusinessData: false);
        var validBackup = Path.Combine(_root, "valid.fgopetbackup");
        await CreateBackupAsync(source, validBackup);
        var before = File.ReadAllBytes(current.Database.DatabasePath);

        var corruptBackup = Path.Combine(_root, "corrupt.fgopetbackup");
        File.WriteAllText(corruptBackup, "not zip");
        var corruptResult = await CreateRestoreService(current, new FakeAgentRuntime()).RestoreAsync(corruptBackup, CancellationToken.None);
        Assert.Equal(BackupRestoreStatus.Rejected, corruptResult.Status);
        Assert.Equal(BackupFailureCode.InvalidManifest, corruptResult.FailureCode);

        var futureBackup = Path.Combine(_root, "future.fgopetbackup");
        File.Copy(validBackup, futureBackup, overwrite: true);
        using (var archive = System.IO.Compression.ZipFile.Open(futureBackup, System.IO.Compression.ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry(BackupFormat.ManifestMember)!;
            string json;
            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                json = reader.ReadToEnd();
            }
            manifestEntry.Delete();
            var manifest = System.Text.Json.JsonSerializer.Deserialize<PrivateBackupManifest>(json)!;
            var future = new PrivateBackupManifest(
                2,
                manifest.ApplicationVersion,
                manifest.DatabaseSchemaVersion,
                manifest.CreatedAtUtc,
                manifest.Files);
            var entry = archive.CreateEntry(BackupFormat.ManifestMember);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(System.Text.Json.JsonSerializer.Serialize(future));
        }

        var futureResult = await CreateRestoreService(current, new FakeAgentRuntime()).RestoreAsync(futureBackup, CancellationToken.None);
        Assert.Equal(BackupRestoreStatus.Rejected, futureResult.Status);
        Assert.Equal(BackupFailureCode.UnsupportedVersion, futureResult.FailureCode);
        Assert.Equal(before, File.ReadAllBytes(current.Database.DatabasePath));
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

    private PrivateBackupRestoreService CreateRestoreService(State current, FakeAgentRuntime runtime)
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
            new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T01:02:03Z")));
    }

    private static State CreateState(string root, bool seedBusinessData)
    {
        var database = new RuntimeDatabase(Path.Combine(root, "runtime.db"));
        new RuntimeDatabaseMigrator(database).Migrate();
        var settings = new JsonAppSettingsStore(root);
        settings.Save(seedBusinessData
            ? AppSettings.Defaults with
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
            }
            : AppSettings.Defaults with { UserProfile = new UserProfile("old") });
        new JsonPackIndexStore(root).Save(seedBusinessData
            ? new PackIndexV1(
                new PortraitSelection("official.mash", "casual", "1.0.0"),
                new PortraitSelection("official.mash", "default", "1.0.0"))
            : PackIndexV1.Empty);

        if (seedBusinessData)
        {
            SeedBusinessData(database);
        }

        SqliteConnection.ClearAllPools();
        return new State(root, database);
    }

    private static void SeedBusinessData(RuntimeDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO focus_presets VALUES('preset-1','builtin',300,60,1,'2026-09-02T00:00:00Z');
            INSERT INTO focus_sessions VALUES('focus-1','completed',300,60,1,1,'focus',0,300,'mash_kyrielight','2026-09-02T00:00:00Z','2026-09-02T00:05:00Z',0);
            INSERT INTO runtime_events(event_id,session_id,type,occurred_at_utc,cycle_number,phase,servant_id,elapsed_seconds,effective_seconds,priority,schema_version,payload_json,source,subject_id,summary,is_private)
            VALUES('event-1','focus-1','focus_completed','2026-09-02T00:05:00Z',1,'focus','mash_kyrielight',300,300,1,1,NULL,'system',NULL,'完成专注',0);
            INSERT INTO timeline_entries VALUES('timeline-1','event-1','2026-09-02T00:05:00Z','focus_completed','mash_kyrielight',300,300,NULL);
            INSERT INTO servant_bonds VALUES('mash_kyrielight',300,1,'bond-v1','2026-09-02T00:05:00Z');
            INSERT INTO conversations VALUES('conversation-1','mash_kyrielight','2026-09-02T00:00:00Z','2026-09-02T00:06:00Z','active',NULL);
            INSERT INTO chat_messages VALUES('message-1','conversation-1','mash_kyrielight',1,'user','你好','completed','2026-09-02T00:01:00Z',NULL);
            INSERT INTO conversation_summaries(summary_id,conversation_id,servant_id,summary_text,covered_through_sequence,created_at_utc,updated_at_utc,binding_id,covered_through_message_id)
            VALUES('summary-1','conversation-1','mash_kyrielight','摘要',1,'2026-09-02T00:02:00Z','2026-09-02T00:02:00Z',NULL,'message-1');
            INSERT INTO memory_candidates(candidate_id,conversation_id,source_message_id,servant_id,appearance_id,candidate_text,status,created_at_utc,reviewed_at_utc)
            VALUES('candidate-1','conversation-1','message-1','mash_kyrielight','casual','候选','approved','2026-09-02T00:03:00Z','2026-09-02T00:04:00Z');
            INSERT INTO memories VALUES('memory-1','mash_kyrielight','记忆',1,'candidate-1','2026-09-02T00:04:00Z','2026-09-02T00:04:00Z');
            INSERT INTO todo_items VALUES('todo-1','任务',NULL,'normal',NULL,'planned','2026-09-02T00:00:00Z','2026-09-02T00:00:00Z',NULL);
            INSERT INTO agent_executions(execution_id,todo_id,source_type,source_instance,task_id,dispatch_request_id,status,started_at_utc,updated_at_utc,ended_at_utc,previous_execution_id,remote_task_id)
            VALUES('execution-1','todo-1','codex','instance-1','task-1','request-1','active','2026-09-02T00:01:00Z','2026-09-02T00:02:00Z',NULL,NULL,'remote-task-1');
            INSERT INTO agent_event_receipts VALUES('codex','instance-1','task-1',1,'task_started','2026-09-02T00:02:00Z',0);
            INSERT INTO work_archives(archive_id,archive_date,source_types,summary,created_at_utc,title,started_on,completed_on,outcomes)
            VALUES('archive-1','2026-09-02','codex','工作归档','2026-09-02T00:10:00Z','归档','2026-09-02','2026-09-02','[]');
            INSERT INTO work_archive_items VALUES('archive-1','todo-1');
            INSERT INTO long_work_archives VALUES('long-1','长期归档','总结','[\"archive-1\"]','2026-09-02T00:11:00Z');
            """;
        command.ExecuteNonQuery();
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
    }
}
