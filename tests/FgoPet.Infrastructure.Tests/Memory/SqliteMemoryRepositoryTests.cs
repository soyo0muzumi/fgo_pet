using FgoPet.App.Memory;
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

    [Fact]
    public void Recall_ranks_indexed_phrase_matches_by_relevance_before_recency()
    {
        var repository = CreateRepository();
        const string repeated = "校准模组 校准模组 校准模组";
        Approve(repository, "strong", repeated, Now());
        Approve(repository, "recent", "新项目记录校准模组", Now().AddDays(1));

        var result = new MemoryRecallService(repository).QueryStrict(new("800100", null), "校准模组");

        Assert.Equal(repeated, result.Items[0].Text);
        Assert.Equal(2, result.Items.Count);
        var recent = result.Items[1];
        Assert.Equal(recent, new MemoryRecallService(repository)
            .QueryStrict(new("800100", null), "校准模组 " + recent.MemoryId).Items[0]);
    }

    [Fact]
    public void Recall_includes_general_and_current_project_memories_only()
    {
        var repository = CreateRepository();
        var database = new RuntimeDatabase(_path, pooling: false);
        CreateConversation(database, "project-a");
        CreateConversation(database, "project-b");
        Approve(repository, "general", "校准模组通用", Now());
        Approve(repository, "own", "校准模组甲项目", Now(), "project-a");
        Approve(repository, "other", "校准模组乙项目", Now(), "project-b");

        var recall = new MemoryRecallService(repository);
        var matching = recall.QueryStrict(new("800100", "project-a"), "校准模组").Items;
        Assert.Equal(new[] { "general", "own" }, matching.Select(item => item.SourceCandidateId).Order());
        Assert.Equal("general", Assert.Single(recall.QueryStrict(new("800100", null), "校准模组").Items).SourceCandidateId);
        Assert.Empty(recall.QueryStrict(new("100001", "project-a"), "校准模组").Items);
    }

    [Fact]
    public void Recall_follows_review_changes_and_short_Chinese_matches()
    {
        var repository = CreateRepository();
        var scope = new MemoryScope("800100", null);
        var recall = new MemoryRecallService(repository);
        repository.AddCandidate(new("pending", "800100", "conversation-1", "校准模组备用", Now()));
        Assert.Empty(recall.QueryStrict(scope, "校准模组").Items);

        Approve(repository, "approved", "避开花生", Now());
        Assert.Empty(recall.QueryStrict(scope, "几何绘图").Items);
        Assert.Equal("避开花生", Assert.Single(recall.Query(scope, "几何绘图").Items).Text);
        var first = recall.QueryStrict(scope, "点餐要避开花生吗");
        var memory = Assert.Single(first.Items);
        Assert.Equal(memory, Assert.Single(recall.QueryStrict(scope, "花生").Items));
        Assert.Empty(recall.QueryStrict(new("100001", null), "避开花生").Items);

        repository.ReviewMemory(memory.MemoryId, "800100", MemoryReviewAction.Edit, "改用芝麻油", Now());
        Assert.Empty(recall.QueryStrict(scope, "花生").Items);
        var edited = recall.QueryStrict(scope, "芝麻油");
        Assert.Equal(memory.Version + 1, Assert.Single(edited.Items).Version);
        Assert.True(edited.Revision > first.Revision);

        repository.ReviewMemory(memory.MemoryId, "800100", MemoryReviewAction.Disable, null, Now());
        Assert.Empty(recall.QueryStrict(scope, "芝麻油").Items);
        Assert.Equal(0L, IndexedHits("芝麻油"));
        repository.ReviewMemory(memory.MemoryId, "800100", MemoryReviewAction.Approve, null, Now());
        Assert.Single(recall.QueryStrict(scope, "芝麻油").Items);
        Assert.Equal(1L, IndexedHits("芝麻油"));
        repository.ReviewMemory(memory.MemoryId, "800100", MemoryReviewAction.Delete, null, Now());
        Assert.Empty(recall.QueryStrict(scope, "芝麻油").Items);
        Assert.Equal(0L, IndexedHits("芝麻油"));
    }

    [Fact]
    public void Compacted_backup_keeps_index_entries_bound_to_their_original_memory()
    {
        var repository = CreateRepository();
        Approve(repository, "first", "早期记录待删除", Now());
        Approve(repository, "second", "保留记录校准模组", Now());
        var first = repository.ListMemories("800100").Single(item => item.SourceCandidateId == "first");
        repository.ReviewMemory(first.MemoryId, "800100", MemoryReviewAction.Delete, null, Now());

        var backupPath = _path + ".backup";
        try
        {
            using (var connection = new RuntimeDatabase(_path, pooling: false).Open())
            using (var vacuum = connection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", backupPath);
                vacuum.ExecuteNonQuery();
            }

            var backup = new RuntimeDatabase(backupPath, pooling: false);
            var recall = new MemoryRecallService(new SqliteMemoryRepository(backup));
            Assert.Equal("second", Assert.Single(recall.QueryStrict(new("800100", null), "校准模组").Items).SourceCandidateId);
            Assert.Equal(0L, IndexedHits("待删除", backupPath));
            using var verify = backup.Open();
            using var integrity = verify.CreateCommand();
            integrity.CommandText = "INSERT INTO memory_search(memory_search,rank) VALUES('integrity-check',1)";
            integrity.ExecuteNonQuery();
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(backupPath + suffix);
        }
    }

    private long IndexedHits(string term, string? path = null)
    {
        using var connection = new RuntimeDatabase(path ?? _path, pooling: false).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memory_search WHERE memory_search MATCH $term";
        command.Parameters.AddWithValue("$term", term);
        return (long)command.ExecuteScalar()!;
    }

    private static void Approve(SqliteMemoryRepository repository, string id, string text, DateTimeOffset at,
        string? project = null)
    {
        repository.AddCandidate(new(id, "800100", project ?? "conversation-1", text, at, projectId: project));
        repository.ReviewCandidate(id, "800100", MemoryReviewAction.Approve, null, at);
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
        CreateConversation(database, null);
        return new SqliteMemoryRepository(database);
    }

    private static void CreateConversation(RuntimeDatabase database, string? project)
    {
        new SqliteConversationRepository(database).CreateConversation(
            project ?? "conversation-1",
            "800100",
            new FgoPet.Core.Dialogue.ContentContextKey(
                "800100", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1"),
            Now(), project);
    }

    private static DateTimeOffset Now() => new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
}
