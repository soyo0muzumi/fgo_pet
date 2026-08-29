using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Memory;

public sealed class SqliteMemoryRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-phase3-memory-{Guid.NewGuid():N}.db");

    [Fact]
    public void Candidate_is_pending_until_reviewed()
    {
        var repository = CreateRepository();
        var candidate = new MemoryCandidate("candidate-1", "800100", "conversation-1", "用户喜欢安静地工作", Now());
        repository.AddCandidate(candidate);

        Assert.Equal(MemoryCandidateStatus.Pending, Assert.Single(repository.ListCandidates("800100")).Status);
    }

    [Fact]
    public void Approved_memory_is_enabled_only_for_its_servant()
    {
        var repository = CreateRepository();
        repository.AddCandidate(new MemoryCandidate("candidate-1", "800100", "conversation-1", "用户喜欢安静地工作", Now()));
        repository.ReviewCandidate("candidate-1", "800100", MemoryReviewAction.Approve, null, Now());

        Assert.Single(repository.ListEnabledMemories("800100"));
        Assert.Empty(repository.ListEnabledMemories("100001"));
    }

    [Fact]
    public void Disabling_a_memory_keeps_it_available_for_review_but_not_injection()
    {
        var repository = CreateRepository();
        repository.AddCandidate(new MemoryCandidate("candidate-1", "800100", "conversation-1", "用户喜欢安静地工作", Now()));
        repository.ReviewCandidate("candidate-1", "800100", MemoryReviewAction.Approve, null, Now());
        var memory = Assert.Single(repository.ListEnabledMemories("800100"));

        repository.ReviewMemory(memory.MemoryId, "800100", MemoryReviewAction.Disable, null, Now());

        Assert.Empty(repository.ListEnabledMemories("800100"));
        Assert.Single(repository.ListMemories("800100"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private SqliteMemoryRepository CreateRepository()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        new SqliteConversationRepository(database).CreateConversation(
            "conversation-1",
            "800100",
            new FgoPet.Core.Dialogue.ContentContextKey(
                "800100", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1"),
            Now());
        return new SqliteMemoryRepository(database);
    }

    private static DateTimeOffset Now() => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
}
