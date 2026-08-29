using FgoPet.App.Memory;
using System.IO;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Memory;

public sealed class ConversationSummaryServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-phase3-summary-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Summary_is_created_only_after_threshold_and_is_bounded()
    {
        var repository = CreateRepository();
        var context = Context();
        repository.CreateConversation("conversation-1", "800100", context, Now());
        for (var index = 1; index <= 6; index++)
        {
            repository.Append(Message($"user-{index}", ChatMessageRole.User, $"用户消息 {index}", index * 2 - 1, context));
            repository.Append(Message($"assistant-{index}", ChatMessageRole.Assistant, $"从者回复 {index}", index * 2, context));
        }

        var service = new ConversationSummaryService(repository, new Settings(memoryEnabled: true), TimeProvider.System, threshold: 4);
        var summary = await service.MaybeSummarizeAsync("conversation-1", "800100", CancellationToken.None);

        Assert.NotNull(summary);
        Assert.InRange(summary!.SummaryText.Length, 1, 6_000);
        Assert.Equal(6, summary.CoveredThroughSequence);
        Assert.Equal("assistant-3", summary.CoveredThroughMessageId);
        Assert.Equal("casual", summary.ContentContext.AppearanceId);
        Assert.Equal("assistant-3", repository.LoadSummary("conversation-1", "800100")!.CoveredThroughMessageId);
    }

    [Fact]
    public async Task Disabled_memory_does_not_create_summary()
    {
        var repository = CreateRepository();
        var context = Context();
        repository.CreateConversation("conversation-1", "800100", context, Now());
        repository.Append(Message("user-1", ChatMessageRole.User, "消息", 1, context));

        var service = new ConversationSummaryService(repository, new Settings(memoryEnabled: false), TimeProvider.System, threshold: 1);

        Assert.Null(await service.MaybeSummarizeAsync("conversation-1", "800100", CancellationToken.None));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _path + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private SqliteConversationRepository CreateRepository()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return new SqliteConversationRepository(database);
    }

    private static ChatMessage Message(string id, ChatMessageRole role, string text, int sequence, ContentContextKey context) =>
        new(id, "conversation-1", "800100", role, text, ChatMessageStatus.Completed, Now(), context, sequence);

    private static ContentContextKey Context() =>
        new("800100", "test-persona", "1.0.0", "casual", "persona-1", "knowledge-1");

    private static DateTimeOffset Now() => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    private sealed class Settings(bool memoryEnabled) : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => AppSettings.Defaults with { MemoryEnabled = memoryEnabled };
        public void Save(AppSettings settings) { }
    }
}
