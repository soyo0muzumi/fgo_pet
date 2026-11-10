using FgoPet.App.Dialogue;
using FgoPet.App.Memory;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Memory.Settings;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Memory;

public sealed class MemoryWriteProtocolTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"memory-protocol-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteMemoryRepository _repository;
    private readonly SqliteConversationRepository _conversations;
    private readonly TestMemorySettings _settings = new();
    private const string Milestone = "我完成了项目验收";
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    public MemoryWriteProtocolTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
        _repository = new(_database, _settings, new ConversationSourceReader(_conversations));
        _repository.StartSession();
    }
    private MemoryWriteTicket Ticket(string id, string? project = null, string text = "我喜欢茶",
        ChatMessageRole role = ChatMessageRole.User)
    {
        _conversations.CreateConversation(id, "mash", Key, DateTimeOffset.UtcNow, project, project);
        var messageId = id + "-message";
        _conversations.Append(new(messageId, id, "mash", role, text, ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, 1));
        var reader = new MemoryExtractionSourceReader(new ConversationSourceReader(_conversations));
        var evidence = role == ChatMessageRole.User ? MemoryEvidenceKind.UserStatement : MemoryEvidenceKind.AssistantSuggestion;
        return _repository.Begin(reader.Read(new(id, "mash", project), messageId, evidence));
    }
    private StoredMemory Approve(MemoryWriteTicket ticket, string text)
    {
        var stage = _repository.Stage(ticket, [new(text)]);
        Assert.Equal(MemoryStageStatus.Staged, stage.Status);
        return _repository.ReviewCandidate(Assert.Single(stage.CandidateIds), "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow)!;
    }

    private static MemoryProposal Episode(MemoryWriteTicket ticket, string text, string quote,
        MemoryOperation operation = MemoryOperation.Create, StoredMemory? target = null, MemoryEvidence? supporting = null) =>
        new(text, target?.MemoryId, target?.Version)
        {
            Lifecycle = new(MemoryContentType.Episodic, operation, supporting is null
                ? [new(ticket.Source.MessageId, quote)]
                : [new(ticket.Source.MessageId, quote), supporting], true),
        };
    [Fact]
    public void Fixed_wording_without_semantic_assessment_cannot_auto_record()
    {
        _settings.Save(new(true, true));
        Assert.Equal(MemoryStageStatus.Staged, _repository.Stage(Ticket("unassessed", "a", Milestone), [new(Milestone)]).Status);
        Assert.Empty(_repository.ListMemories("mash"));
    }

    [Fact]
    public void Unreviewed_milestones_require_consent_when_issued_and_when_committed()
    {
        var beforeOptIn = Ticket("before-opt-in", "a", Milestone);
        _settings.Save(new(true, true));
        var old = _repository.Stage(beforeOptIn, [Episode(beforeOptIn, Milestone, Milestone)]);
        Assert.Equal(MemoryStageStatus.Staged, old.Status);
        var authorized = Ticket("authorized", "b", Milestone);
        _settings.Save(new(true, false));
        Assert.Equal(MemoryStageStatus.Staged, _repository.Stage(authorized, [Episode(authorized, Milestone, Milestone)]).Status);
        Assert.All(_repository.ListCandidates("mash"), candidate => Assert.Equal(MemoryContentType.Episodic, candidate.ContentType));
        Assert.Empty(_repository.ListMemories("mash"));
    }

    [Fact]
    public void Authorized_whole_message_is_recorded_and_only_recalled_as_history_until_confirmed()
    {
        _settings.Save(new(true, true));
        var ticket = Ticket("milestone", "a", Milestone + "。");
        var otherQueued = Ticket("queued-before-record", "a", "我完成了原型演示");
        var recall = new MemoryRecallService(_repository);
        var scope = new MemoryScope("mash", "a");
        var before = recall.QueryHistory(scope, "项目验收").Revision;
        var stage = _repository.Stage(ticket, [Episode(ticket, "用户完成了项目验收", Milestone)]);
        Assert.Equal(MemoryStageStatus.Recorded, stage.Status);
        Assert.Empty(stage.CandidateIds);
        Assert.Empty(_repository.ListCandidates("mash"));
        var history = recall.QueryHistory(scope, "项目验收");
        Assert.True(history.Revision > before);
        var record = Assert.Single(history.Items);
        Assert.Equal("用户完成了项目验收", record.Text);
        Assert.Equal(ticket.Source, record.Source);
        Assert.Equal(MemoryContentType.Episodic, record.ContentType);
        Assert.Equal(MemoryVerification.Recorded, record.Verification);
        Assert.Empty(recall.QueryStrict(scope, "项目验收").Items);
        Assert.Equal(record.MemoryId, Assert.Single(recall.QueryForExtraction(scope, "项目验收").Items).MemoryId);
        Assert.Empty(recall.QueryHistory(new("mash", "b"), "项目验收").Items);
        Assert.Empty(recall.QueryHistory(new("other", "a"), "项目验收").Items);
        Assert.Equal(MemoryStageStatus.Stale, _repository.Stage(otherQueued, [new("我完成了原型演示")]).Status);
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(ticket, [new(Milestone)]).Status);
        var repeated = Ticket("repeated", "a", Milestone);
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(repeated,
            [Episode(repeated, "项目验收已经通过", Milestone, MemoryOperation.Duplicate, record)]).Status);

        _repository.ReviewMemory(record.MemoryId, "mash", MemoryReviewAction.Disable, null, DateTimeOffset.UtcNow, record.Version);
        Assert.Empty(recall.QueryHistory(scope, "项目验收").Items);
        Assert.Throws<MemoryReviewException>(() =>
            _repository.ReviewMemory(record.MemoryId, "mash", MemoryReviewAction.Confirm, null, DateTimeOffset.UtcNow, record.Version));
        var disabled = Assert.Single(_repository.ListMemories("mash"));
        _repository.ReviewMemory(record.MemoryId, "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow, disabled.Version);
        var reenabled = Assert.Single(recall.QueryHistory(scope, "项目验收").Items);
        Assert.Equal(MemoryVerification.Recorded, reenabled.Verification);
        _repository.ReviewMemory(record.MemoryId, "mash", MemoryReviewAction.Confirm, null, DateTimeOffset.UtcNow, reenabled.Version);
        _conversations.DeleteConversation("milestone", "mash");
        var confirmed = Assert.Single(recall.QueryStrict(scope, "项目验收").Items);
        Assert.Equal(MemoryVerification.Confirmed, confirmed.Verification);
        Assert.Equal(reenabled.Version + 1, confirmed.Version);
        Assert.False(confirmed.SourceAvailable);
    }

    [Theory]
    [InlineData("我完成了项目验收。我的病历编号是 12345。", Milestone, "a", ChatMessageRole.User)]
    [InlineData("我完成了项目验收。Token: synthetic-secret", Milestone, "a", ChatMessageRole.User)]
    [InlineData("修好了", Milestone, "a", ChatMessageRole.User)]
    [InlineData("我一直偏好清淡饮食", "我一直偏好清淡饮食", "a", ChatMessageRole.User)]
    [InlineData("更正一下，我完成了项目验收", Milestone, "a", ChatMessageRole.User)]
    [InlineData(Milestone, Milestone, "a", ChatMessageRole.Assistant)]
    [InlineData(Milestone, Milestone, null, ChatMessageRole.User)]
    public void Unverified_or_sensitive_source_cannot_write_a_record(string original, string proposal, string? project, ChatMessageRole role)
    {
        _settings.Save(new(true, true));
        var stage = _repository.Stage(Ticket("review", project, original, role), [new(proposal)]);
        Assert.Equal(MemoryStageStatus.Staged, stage.Status);
        Assert.Empty(_repository.ListMemories("mash"));
        Assert.Single(_repository.ListCandidates("mash"));
    }

    [Fact]
    public void A_proposed_replacement_stays_pending_even_when_its_text_is_allowlisted()
    {
        _settings.Save(new(true, true));
        var memory = Approve(Ticket("original", "a"), "项目验收仍未完成");
        var replacement = new MemoryProposal(Milestone, memory.MemoryId, memory.Version);
        Assert.Equal(MemoryStageStatus.Staged,
            _repository.Stage(Ticket("replacement", "a", Milestone), [replacement]).Status);
        Assert.Equal(memory, Assert.Single(_repository.ListMemories("mash")));
    }

    [Fact]
    public void Deleting_a_recorded_source_purges_the_record_and_prevents_replay()
    {
        _settings.Save(new(true, true));
        var ticket = Ticket("source", "a", Milestone);
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(ticket, [Episode(ticket, Milestone, Milestone)]).Status);
        var recall = new MemoryRecallService(_repository);
        var scope = new MemoryScope("mash", "a");
        var prior = recall.QueryHistory(scope, Milestone);
        Assert.Single(prior.Items);
        _conversations.DeleteConversation("source", "mash");
        var after = recall.QueryHistory(scope, Milestone);
        Assert.Empty(after.Items);
        Assert.True(after.Revision > prior.Revision);
        Assert.Empty(_repository.ListMemories("mash"));
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(ticket, [new(Milestone)]).Status);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memory_search WHERE memory_search MATCH '项目验收'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(200, 10)]
    [InlineData(20, 2000)]
    public void Auto_recording_stops_at_either_role_capacity_limit(int count, int length)
    {
        for (var i = 0; i < count; i++)
            Approve(Ticket("existing-" + i), i.ToString().PadLeft(length, '字'));

        _settings.Save(new(true, true));
        var ticket = Ticket("over-capacity", "a", Milestone);
        var stage = _repository.Stage(ticket, [Episode(ticket, Milestone, Milestone)]);
        Assert.Equal(MemoryStageStatus.CapacityExceeded, stage.Status);
        Assert.Equal(count, _repository.ListMemories("mash").Count);
        Assert.All(_repository.ListMemories("mash"), item => Assert.Equal(MemoryVerification.Confirmed, item.Verification));
    }

    [Fact]
    public void Transitions_retain_history_and_related_recall_prioritizes_the_new_state()
    {
        _settings.Save(new(true, true));
        var first = Ticket("preview", "a", "我修通了月灯的预览页");
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(first,
            [Episode(first, "月灯的预览页恢复正常", "我修通了月灯的预览页")]).Status);
        var past = Assert.Single(_repository.ListMemories("mash"));
        var second = Ticket("regression", "a", "重新部署后，月灯预览再次报错");
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(second,
            [Episode(second, "月灯重新部署后预览报错", "重新部署后，月灯预览再次报错", MemoryOperation.Transition, past)]).Status);
        var items = new MemoryRecallService(_repository).QueryHistory(new("mash", "a"), "恢复正常").Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(past.MemoryId, items[0].PreviousMemoryId);
        Assert.Equal(MemoryOperation.Transition, items[0].Relation);
        Assert.True(items[1].HasNewerState);
        Assert.Equal(past.Text, items[1].Text);
        Assert.Equal(past.Version, items[1].Version);
    }

    [Fact]
    public void Extending_a_record_keeps_its_original_evidence_and_never_overwrites_confirmed_text()
    {
        _settings.Save(new(true, true));
        var first = Ticket("export-test", "a", "我重跑了月灯导出测试");
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(first,
            [Episode(first, "用户重跑了月灯导出测试", "我重跑了月灯导出测试")]).Status);
        var original = Assert.Single(_repository.ListMemories("mash"));
        var detail = Ticket("export-detail", "a", "月灯导出测试有三个样例通过");
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(detail,
            [Episode(detail, "用户重跑了月灯导出测试，三个样例通过", "月灯导出测试有三个样例通过",
                MemoryOperation.Extend, original)]).Status);
        var extended = Assert.Single(_repository.ListMemories("mash"));
        Assert.Equal(original.MemoryId, extended.MemoryId);
        Assert.Equal(original.Version + 1, extended.Version);
        _repository.ReviewMemory(extended.MemoryId, "mash", MemoryReviewAction.Confirm, null, DateTimeOffset.UtcNow, extended.Version);
        var confirmed = Assert.Single(_repository.ListMemories("mash"));
        var supplement = Ticket("export-format", "a", "月灯导出测试的三个样例都是 PDF");
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(supplement,
            [Episode(supplement, "月灯导出测试的三个样例都是 PDF", "月灯导出测试的三个样例都是 PDF",
                MemoryOperation.Extend, confirmed)]).Status);
        var items = _repository.ListMemories("mash");
        Assert.Equal(confirmed, Assert.Single(items.Where(m => m.Verification == MemoryVerification.Confirmed)));
        Assert.Equal(confirmed.MemoryId, Assert.Single(items.Where(m => m.Verification == MemoryVerification.Recorded)).PreviousMemoryId);
        _conversations.DeleteConversation("export-detail", "mash");
        Assert.False(_repository.ListMemories("mash").Single(m => m.MemoryId == confirmed.MemoryId).SourceAvailable);
    }

    [Fact]
    public void A_queued_ticket_can_refresh_its_snapshot_but_cannot_cross_a_revocation()
    {
        var queued = Ticket("waiting", "a");
        _ = Approve(Ticket("edited", "a"), "另一条已确认记忆");
        var refreshed = Assert.IsType<MemoryWriteTicket>(_repository.Refresh(queued));
        Assert.True(refreshed.MemoryRevision > queued.MemoryRevision);
        Assert.Equal(queued.Generation, refreshed.Generation);
        _repository.InvalidateWrites();
        Assert.Null(_repository.Refresh(refreshed));
    }

    [Fact]
    public void Removing_a_required_secondary_source_cleans_fts_and_invalidates_all_linked_receipts()
    {
        _settings.Save(new(true, true));
        var earlier = Ticket("multiturn", "a", "月灯导出有个卡住的问题");
        _conversations.Append(new("finished", "multiturn", "mash", ChatMessageRole.User, "总算搞定了",
            ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, 2));
        var sourceReader = new MemoryExtractionSourceReader(new ConversationSourceReader(_conversations));
        var ticket = _repository.Begin(sourceReader.Read(new("multiturn", "mash", "a"), "finished", MemoryEvidenceKind.UserStatement));
        Assert.Equal(MemoryStageStatus.Recorded, _repository.Stage(ticket,
            [Episode(ticket, "用户解决了月灯导出卡住的问题", "总算搞定了",
                supporting: new(earlier.Source.MessageId, "月灯导出有个卡住的问题"))]).Status);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_messages WHERE message_id=$message";
        command.Parameters.AddWithValue("$message", earlier.Source.MessageId);
        Assert.Equal(1, command.ExecuteNonQuery());
        Assert.Empty(new MemoryRecallService(_repository).QueryHistory(new("mash", "a"), "月灯导出").Items);
        Assert.Empty(_repository.ListMemories("mash"));
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(ticket, [new(Milestone)]).Status);
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

    [Fact]
    public void Candidate_notice_only_follows_a_committed_nonempty_stage()
    {
        var ticket = Ticket("notice", "fixture-project");
        var notices = new List<ToolScope>();
        _repository.CandidatesStaged += scope =>
        {
            if (_repository.ListCandidates(scope.RoleId).Any(c => c.ConversationId == scope.ConversationId && c.Status == MemoryCandidateStatus.Pending))
                notices.Add(scope);
        };

        _repository.Stage(Ticket("empty"), []);
        _repository.Stage(ticket, [new("我偏好无糖茶")]);
        _repository.Stage(ticket, [new("我偏好无糖茶")]);

        Assert.Equal(new ToolScope("notice", "mash", "fixture-project"), Assert.Single(notices));
        Assert.Empty(_repository.ListEnabledMemories("mash"));
    }

    [Fact]
    public void Staging_a_pending_candidate_does_not_discard_another_turn_already_queued()
    {
        var first = Ticket("first");
        var second = Ticket("second", text: "我偏好纸质笔记");
        var duplicate = Ticket("duplicate");

        Assert.Equal(MemoryStageStatus.Staged, _repository.Stage(first, [new("我喜欢茶")]).Status);
        Assert.Equal(MemoryStageStatus.Staged, _repository.Stage(second, [new("我偏好纸质笔记")]).Status);
        Assert.Equal(MemoryStageStatus.Duplicate, _repository.Stage(duplicate, [new("我喜欢茶")]).Status);
        Assert.Equal(2, _repository.ListCandidates("mash").Count);
        Assert.Empty(_repository.ListEnabledMemories("mash"));
    }

    [Fact]
    public void Approving_a_candidate_still_invalidates_extraction_against_the_old_confirmed_snapshot()
    {
        var first = _repository.Stage(Ticket("first"), [new("我喜欢茶")]);
        var queued = Ticket("queued", text: "我偏好纸质笔记");
        _repository.ReviewCandidate(Assert.Single(first.CandidateIds), "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow);

        Assert.Equal(MemoryStageStatus.Stale, _repository.Stage(queued, [new("我偏好纸质笔记")]).Status);
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

    private sealed class TestMemorySettings : IMemorySettingsStore
    {
        private MemorySettings _settings = MemorySettings.Defaults;
        public MemorySettings Load() => _settings;
        public void Save(MemorySettings settings) => _settings = settings;
    }
}
