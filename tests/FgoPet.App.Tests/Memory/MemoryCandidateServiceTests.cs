using FgoPet.App.Memory;
using System.IO;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Memory;

public sealed class MemoryCandidateServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-phase3-memory-service-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Approved_memory_is_scoped_to_servant_and_survives_appearance_change()
    {
        var database = CreateDatabase();
        var repository = new SqliteMemoryRepository(database);
        var conversations = new FgoPet.Infrastructure.Dialogue.SqliteConversationRepository(database);
        var context = new ContentContextKey("800100", "test", "1.0.0", "casual", "p1", "k1");
        conversations.CreateConversation("conversation-1", "800100", context, DateTimeOffset.UtcNow);
        repository.AddCandidate(new MemoryCandidate("candidate-1", "800100", "conversation-1", "用户喜欢安静工作。", DateTimeOffset.UtcNow, appearanceId: "casual"));

        var service = new MemoryCandidateService(repository, TimeProvider.System);
        await service.ReviewAsync("800100", "candidate-1", MemoryReviewAction.Approve, null, CancellationToken.None);

        var memories = await service.ListEnabledAsync("800100", CancellationToken.None);

        Assert.Single(memories);
        Assert.Equal("800100", memories[0].ServantId);
    }

    [Fact]
    public async Task Candidate_review_cannot_cross_servant_boundary()
    {
        var database = CreateDatabase();
        var repository = new SqliteMemoryRepository(database);
        var conversations = new FgoPet.Infrastructure.Dialogue.SqliteConversationRepository(database);
        var context = new ContentContextKey("800100", "test", "1.0.0", "casual", "p1", "k1");
        conversations.CreateConversation("conversation-1", "800100", context, DateTimeOffset.UtcNow);
        repository.AddCandidate(new MemoryCandidate("candidate-1", "800100", "conversation-1", "隔离内容", DateTimeOffset.UtcNow));

        var service = new MemoryCandidateService(repository, TimeProvider.System);

        await service.ReviewAsync("100001", "candidate-1", MemoryReviewAction.Approve, null, CancellationToken.None);

        Assert.Empty(await service.ListEnabledAsync("100001", CancellationToken.None));
        Assert.Empty(await service.ListEnabledAsync("800100", CancellationToken.None));
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

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
    }
}
