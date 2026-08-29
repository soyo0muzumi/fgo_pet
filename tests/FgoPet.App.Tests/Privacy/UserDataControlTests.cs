using System.IO.Compression;
using System.IO;
using System.Text;
using FgoPet.App.Privacy;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.App.Tests.Privacy;

public sealed class UserDataControlTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-phase3-privacy-{Guid.NewGuid():N}.db");
    private readonly string _exportPath = Path.Combine(Path.GetTempPath(), $"fgo-phase3-export-{Guid.NewGuid():N}.zip");

    [Fact]
    public async Task Export_excludes_secrets_prompts_raw_story_and_absolute_paths()
    {
        var database = CreateDatabase();
        var conversations = new SqliteConversationRepository(database);
        var memories = new SqliteMemoryRepository(database);
        var context = new ContentContextKey("800100", "test", "1.0.0", "casual", "p1", "k1");
        conversations.CreateConversation("conversation-1", "800100", context, DateTimeOffset.UtcNow);
        conversations.Append(new ChatMessage("message-1", "conversation-1", "800100", ChatMessageRole.User, "你好", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, context, 1));
        memories.AddCandidate(new MemoryCandidate("candidate-1", "800100", "conversation-1", "可导出的候选", DateTimeOffset.UtcNow));

        await new UserDataExportService(database).ExportAsync(_exportPath, CancellationToken.None);

        using var archive = ZipFile.OpenRead(_exportPath);
        var text = string.Join('\n', archive.Entries.Select(entry =>
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }));
        Assert.Contains("export_version", text);
        Assert.Contains("可导出的候选", text);
        Assert.DoesNotContain("api-key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(_path), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_conversation_keeps_approved_memory_but_all_data_deletion_removes_both()
    {
        var database = CreateDatabase();
        var conversations = new SqliteConversationRepository(database);
        var memories = new SqliteMemoryRepository(database);
        var context = new ContentContextKey("800100", "test", "1.0.0", "casual", "p1", "k1");
        conversations.CreateConversation("conversation-1", "800100", context, DateTimeOffset.UtcNow);
        memories.AddCandidate(new MemoryCandidate("candidate-1", "800100", "conversation-1", "已确认记忆", DateTimeOffset.UtcNow));
        memories.ReviewCandidate("candidate-1", "800100", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow);
        var deletion = new UserDataDeletionService(database, conversations, memories);

        await deletion.DeleteConversationAsync("conversation-1", "800100", CancellationToken.None);

        Assert.Single(memories.ListEnabledMemories("800100"));
        await deletion.DeleteAllAsync(CancellationToken.None);
        Assert.Empty(memories.ListEnabledMemories("800100"));
    }

    [Fact]
    public async Task All_data_deletion_clears_profile_packages_and_model_metadata_but_preserves_phase2_history()
    {
        var database = CreateDatabase();
        var conversations = new SqliteConversationRepository(database);
        var memories = new SqliteMemoryRepository(database);
        var settings = new FakeSettingsStore
        {
            Current = AppSettings.Defaults with
            {
                UserProfile = new UserProfile("xqj"),
                PackageSettings = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["mash_kyrielight"] = new Dictionary<string, string> { ["show_status"] = "true" },
                },
                ModelConnection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
                ServantPreferences = new Dictionary<string, ServantPreference>
                {
                    ["mash_kyrielight"] = new ServantPreference(AddressMode.UserDefined, "御主"),
                },
            },
        };
        var credentials = new FakeCredentialStore();
        InsertPhase2Bond(database);
        var deletion = new UserDataDeletionService(database, conversations, memories, credentials, settings);

        await deletion.DeleteAllAsync(CancellationToken.None);

        Assert.Null(settings.Current.UserProfile);
        Assert.Empty(settings.Current.PackageSettings);
        Assert.Null(settings.Current.ModelConnection);
        Assert.Empty(settings.Current.ServantPreferences);
        Assert.Equal(["fgo-pet/provider/openai"], credentials.DeletedTargets);
        Assert.Equal(1L, CountPhase2Bonds(database));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm", _exportPath })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
    }

    private static void InsertPhase2Bond(RuntimeDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO servant_bonds(servant_id, lifetime_focus_seconds, achieved_level, policy_version, updated_at_utc)
            VALUES('mash_kyrielight', 1500, 1, 'bond-v1', '2026-08-29T00:00:00Z');
            """;
        command.ExecuteNonQuery();
    }

    private static long CountPhase2Bonds(RuntimeDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM servant_bonds";
        return (long)command.ExecuteScalar()!;
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public string Location => "memory";

        public AppSettings Current { get; set; } = AppSettings.Defaults;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings) => Current = settings;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public List<string> DeletedTargets { get; } = new();

        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task DeleteAsync(string target, CancellationToken cancellationToken)
        {
            DeletedTargets.Add(target);
            return Task.CompletedTask;
        }
    }
}
