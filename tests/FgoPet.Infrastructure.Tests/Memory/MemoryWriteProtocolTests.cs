using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Memory;

public sealed class MemoryWriteProtocolTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"memory-protocol-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteMemoryRepository _repository;
    private readonly SqliteConversationRepository _conversations;
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    public MemoryWriteProtocolTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
        _repository = new(_database);
        _repository.StartSession();
    }
    private MemoryWriteTicket Ticket(string id, string? project = null)
    {
        _conversations.CreateConversation(id, "mash", Key, DateTimeOffset.UtcNow, project, project);
        _conversations.Append(new(id + "-user", id, "mash", ChatMessageRole.User, "我喜欢茶", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, 1));
        return _repository.Begin(new(new("mash", project), id, id + "-user", "fingerprint-" + id, DateTimeOffset.UtcNow, MemoryEvidenceKind.UserStatement));
    }
    private StoredMemory Approve(MemoryWriteTicket ticket, string text)
    {
        var stage = _repository.Stage(ticket, [new(text)]);
        Assert.Equal(MemoryStageStatus.Staged, stage.Status);
        return _repository.ReviewCandidate(Assert.Single(stage.CandidateIds), "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow)!;
    }
    [Fact]
    public void Exact_identity_preserves_negation_case_and_normalizes_only_spacing_and_unicode()
    {
        Assert.Equal("我喜欢茶", MemoryTextIdentity.Normalize("  我喜欢茶\r\n"));
        Assert.Equal("每次 25 分钟", MemoryTextIdentity.Normalize("每次   25 分钟"));
        Assert.NotEqual(MemoryTextIdentity.Normalize("我喜欢茶"), MemoryTextIdentity.Normalize("我不喜欢茶"));
        Assert.NotEqual(MemoryTextIdentity.Normalize("Tea"), MemoryTextIdentity.Normalize("tea"));
        Assert.Equal(MemoryTextIdentity.Normalize("é"), MemoryTextIdentity.Normalize("e\u0301"));
    }
    [Fact]
    public void Receipt_and_text_deduplication_and_repeated_approval_are_idempotent()
    {
        var ticket = Ticket("c");
        var staged = _repository.Stage(ticket, [new("每次 25 分钟")]);
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(ticket, [new("另一内容")]).Status);
        var candidate = Assert.Single(_repository.ListCandidates("mash"));
        Assert.Empty(_repository.ListEnabledMemories("mash"));
        var approved = _repository.ReviewCandidate(candidate.CandidateId, "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow)!;
        var repeated = _repository.ReviewCandidate(candidate.CandidateId, "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow)!;
        Assert.Equal(approved.MemoryId, repeated.MemoryId);
        Assert.Single(_repository.ListMemories("mash"));
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(Ticket("d"), [new(" 每次   25 分钟 ")]).Status);
        Assert.Single(_repository.ListCandidates("mash"));
    }
    [Theory]
    [InlineData("session")]
    [InlineData("invalidate")]
    [InlineData("delete-source")]
    public void Late_ticket_is_rejected_after_invalidation_or_source_deletion(string operation)
    {
        var ticket = Ticket("c");
        if (operation == "session") _repository.StartSession();
        if (operation == "invalidate") _repository.InvalidateWrites();
        if (operation == "delete-source") _conversations.DeleteConversation("c", "mash");
        Assert.Equal(MemoryStageStatus.Stale, _repository.Stage(ticket, [new("迟到结果")]).Status);
        Assert.Empty(_repository.ListCandidates("mash"));
    }
    [Fact]
    public void Exact_replacement_preserves_id_updates_provenance_and_rejects_conflicts_and_cross_project()
    {
        var memory = Approve(Ticket("c", "a"), "我喜欢茶");
        Assert.Equal(MemoryEvidenceKind.UserStatement, memory.Source!.Kind);
        var update = _repository.Stage(Ticket("d", "a"), [new("我偏好咖啡", memory.MemoryId, memory.Version)]);
        var candidate = Assert.Single(update.CandidateIds);
        var changed = _repository.ReviewCandidate(candidate, "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow)!;
        Assert.Equal(memory.MemoryId, changed.MemoryId);
        Assert.Equal(2, changed.Version);
        Assert.Equal("d", changed.Source!.ConversationId);
        Assert.Equal("我偏好咖啡", Assert.Single(_repository.ListEnabledMemories("mash")).Text);
        Assert.Equal(MemoryStageStatus.Invalid, _repository.Stage(Ticket("e", "b"), [new("跨项目", changed.MemoryId, changed.Version)]).Status);
        var stale = _repository.Stage(Ticket("f", "a"), [new("旧更正", changed.MemoryId, changed.Version)]);
        _repository.ReviewMemory(changed.MemoryId, "mash", MemoryReviewAction.Edit, "新编辑", DateTimeOffset.UtcNow);
        Assert.Throws<MemoryReviewException>(() => _repository.ReviewCandidate(stale.CandidateIds.Single(), "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow));
        Assert.Equal("新编辑", Assert.Single(_repository.ListMemories("mash")).Text);
    }
    [Fact]
    public void Invalid_batch_is_atomic_and_deleted_source_cannot_recreate_memory()
    {
        var ticket = Ticket("c");
        var revision = _repository.ReadSnapshot(new("mash", null)).Revision;
        Assert.Equal(MemoryStageStatus.Invalid, _repository.Stage(ticket, [new("valid"), new("invalid", "missing", 1)]).Status);
        Assert.Empty(_repository.ListCandidates("mash"));
        Assert.Equal(revision, _repository.ReadSnapshot(new("mash", null)).Revision);
        var memory = Approve(ticket, "保留偏好");
        _repository.ReviewMemory(memory.MemoryId, "mash", MemoryReviewAction.Delete, null, DateTimeOffset.UtcNow);
        var replay = _repository.Begin(ticket.Source);
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(replay, [new("保留偏好")]).Status);
        Assert.Empty(_repository.ListMemories("mash"));
    }
    [Fact]
    public void Capacity_counts_disabled_memories_and_never_evicts_existing_data()
    {
        for (var i = 0; i < 200; i++) Approve(Ticket("c" + i), "偏好 " + i);
        var first = _repository.ListMemories("mash")[0];
        _repository.ReviewMemory(first.MemoryId, "mash", MemoryReviewAction.Disable, null, DateTimeOffset.UtcNow);
        Assert.Equal(MemoryStageStatus.CapacityExceeded, _repository.Stage(Ticket("overflow"), [new("额外偏好")]).Status);
        Assert.Equal(200, _repository.ListMemories("mash").Count);
    }
    [Fact]
    public void Source_deletion_preserves_approved_memory_but_marks_provenance_unavailable()
    {
        var memory = Approve(Ticket("c", "a"), "项目偏好");
        _conversations.DeleteConversation("c", "mash");
        var stored = Assert.Single(_repository.ListMemories("mash"));
        Assert.Equal(memory.MemoryId, stored.MemoryId);
        Assert.False(stored.SourceAvailable);
        Assert.Equal("c", stored.Source!.ConversationId);
        Assert.Empty(_repository.ReadSnapshot(new("mash", "b")).Items);
        Assert.Single(_repository.ReadSnapshot(new("mash", "a")).Items);
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }
}
