using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using FgoPet.App.Dialogue;
using FgoPet.App.Memory;
using FgoPet.App.Privacy;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Memory.Settings;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed partial class MemoryLifecycleEndToEndTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"memory-lifecycle-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteMemoryRepository _repository;
    private readonly SqliteConversationRepository _conversations;
    private readonly DialogueContextLifetime _lifetime = new();
    private readonly Settings _settings = new();
    private readonly Provider _provider = new();
    private readonly ObservingSink _sink;
    private readonly MemoryCandidateService _review;
    private readonly MemoryExtractionQueue _queue;
    private readonly ConversationOrchestrator _chat;
    private static readonly ConversationRequestContext Project = new("p", "研究项目");
    public MemoryLifecycleEndToEndTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
        _repository = new(_database, _settings, new ConversationSourceReader(_conversations)); _repository.StartSession();
        _sink = new(_repository); _review = new(_repository, TimeProvider.System);
        var meter = new RequestTokenMeter();
        var limit = new ModelContextResolver(_ => _provider, TimeProvider.System);
        var recall = new MemoryRecallService(_repository);
        _queue = new(new ProviderMemoryCandidateExtractor(_ => _provider, limit, meter, recall, _conversations), _sink, _repository, _lifetime,
            connection => _settings.Enabled && connection == _settings.Load().ModelConnection,
            isSourceCurrent: source => new MemoryExtractionSourceReader(new ConversationSourceReader(_conversations)).IsCurrent(source));
        _chat = new(new Resolver(_provider), new Content(), _conversations, new(meter), TimeProvider.System,
            settings: _settings, contextResolver: limit, tokenMeter: meter, lifetime: _lifetime,
            capabilities: FgoPet.Testing.ActivatedCapabilities.Create(new FgoPet.Plugin.Memory.MemoryPlugin(recall,
                new ConversationSourceReader(_conversations), _sink, _queue, () => _settings.Enabled),
                FgoPet.Testing.ActivatedCapabilities.Todo(_database)));
    }
    [Fact]
    public async Task Authorized_multiturn_experience_is_available_in_the_next_conversation_without_upgrading_its_authority()
    {
        _settings.AutoRecord = true;
        await Send("我正在修月灯的导出按钮。");
        Assert.Equal(MemoryStageStatus.Recorded, (await Send("总算搞定了", startNew: false))!.Status);
        var memory = Assert.Single(_repository.ListMemories("mash"));
        Assert.Equal(MemoryVerification.Recorded, memory.Verification);
        await Send("月灯的导出按钮进展如何？");
        Assert.Contains("未审核经历", Assert.Single(MemoryBlocks()));
        await Send("假设月灯的导出按钮已经发布。");
        Assert.Equal(memory, Assert.Single(_repository.ListMemories("mash")));
    }

    [Fact]
    public async Task User_candidate_confirmation_correction_disable_delete_are_reflected_in_actual_next_requests()
    {
        await Send("我喜欢茶");
        var candidate = Assert.Single(_repository.ListCandidates("mash"));
        Assert.Equal(MemoryEvidenceKind.UserStatement, candidate.Source!.Kind);
        Assert.Equal("p", candidate.ProjectId);
        Assert.Equal("研究项目", candidate.Source.ProjectLabel);
        Assert.Equal(ChatMessageRole.User, _conversations.LoadMessages(candidate.ConversationId, "mash").Single(m => m.MessageId == candidate.SourceMessageId).Role);
        await Send("喜欢茶的偏好是什么");
        Assert.Empty(MemoryBlocks());
        var approved = (await _review.ReviewAsync("mash", candidate.CandidateId, MemoryReviewAction.Approve, null, default))!;
        await Send("喜欢茶的偏好是什么");
        Assert.Contains("我喜欢茶", Assert.Single(MemoryBlocks()));
        Assert.Contains("来源：UserStatement", Assert.Single(MemoryBlocks()));
        await Send("我不再喜欢茶，改为偏好咖啡");
        var correction = Assert.Single(_repository.ListCandidates("mash").Where(c => c.Status == MemoryCandidateStatus.Pending));
        Assert.Equal(approved.MemoryId, correction.ReplacesMemoryId);
        var changed = (await _review.ReviewAsync("mash", correction.CandidateId, MemoryReviewAction.Approve, null, default))!;
        Assert.Equal(approved.MemoryId, changed.MemoryId);
        Assert.Equal(2, changed.Version);
        Assert.Equal(correction.Source, changed.Source);
        Assert.NotEqual(approved.Source!.MessageId, changed.Source!.MessageId);
        await Send("偏好咖啡的记录");
        Assert.Contains("偏好咖啡", Assert.Single(MemoryBlocks()));
        Assert.DoesNotContain(MemoryBlocks(), text => text.Contains("喜欢茶"));
        await _review.ReviewMemoryAsync("mash", changed.MemoryId, MemoryReviewAction.Disable, null, default, changed.Version);
        await Send("偏好咖啡的记录");
        Assert.Empty(MemoryBlocks());
        await _review.ReviewMemoryAsync("mash", changed.MemoryId, MemoryReviewAction.Delete, null, default);
        await Send("偏好咖啡的记录");
        Assert.Empty(MemoryBlocks());
        Assert.Empty(_repository.ListMemories("mash"));
        Assert.All(_provider.Extractions, request => { Assert.Null(request.Tools); Assert.InRange(request.MaxOutputTokens!.Value, 1, 1024); });
        Assert.All(_provider.Extractions, request => Assert.Equal("memory_extraction", request.Metadata["fgo_auxiliary"]));
    }
    [Fact]
    public async Task Short_update_uses_the_previous_completed_turn_and_keeps_dated_milestones_pending()
    {
        _provider.MainText = "导出功能修好了吗？";
        await Send("导出功能昨天进入验收。");
        await Send("修好了", startNew: false);
        var extraction = _provider.Extractions[^1];
        var repeat = await Send("修好了", startNew: false);
        Assert.Equal(MemoryStageStatus.Duplicate, repeat!.Status);

        var candidates = _repository.ListCandidates("mash");
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(MemoryCandidateStatus.Pending, candidate.Status));
        var milestone = Assert.Single(candidates.Where(candidate => candidate.Text.Contains("进入验收")));
        var fix = Assert.Single(candidates.Where(candidate => candidate.Text.Contains("已修复")));
        Assert.Contains("昨天（相对于用户陈述时间）", milestone.Text);
        Assert.Contains("截至用户陈述时", fix.Text);
        var source = _conversations.LoadMessages(fix.ConversationId, "mash").Single(m => m.MessageId == fix.SourceMessageId);
        Assert.Equal("修好了", source.Text);
        Assert.Equal(source.CreatedAtUtc, fix.Source!.OccurredAtUtc);
        using var input = JsonDocument.Parse(extraction.Messages[^1].Text);
        Assert.Equal(source.CreatedAtUtc, input.RootElement.GetProperty("source_at_utc").GetDateTimeOffset());
        Assert.Empty(_repository.ListMemories("mash"));
    }

    [Fact]
    public async Task Earlier_turns_and_other_conversations_cannot_supply_candidate_evidence()
    {
        _provider.MainText = "导出功能修好了吗？";
        await Send("导出功能昨天进入验收。");
        _provider.MainText = "猜测用户偏好蓝色。";
        _provider.ExtractionMode = "previous-turn-evidence";
        await Send("今天的午餐吃面。", startNew: false);
        using var sameConversation = JsonDocument.Parse(_provider.Extractions[^1].Messages[^1].Text);
        var prior = Assert.Single(sameConversation.RootElement.GetProperty("recent_context_only").EnumerateArray());
        Assert.Equal("导出功能修好了吗？", prior.GetProperty("assistant").GetString());
        await Send("今天的午餐吃面。");
        using var otherConversation = JsonDocument.Parse(_provider.Extractions[^1].Messages[^1].Text);
        Assert.Empty(otherConversation.RootElement.GetProperty("recent_context_only").EnumerateArray());
        Assert.Single(_repository.ListCandidates("mash"));
        Assert.Empty(_repository.ListMemories("mash"));
    }
    [Theory]
    [InlineData("delete")]
    [InlineData("disable")]
    [InlineData("restore")]
    [InlineData("exit")]
    [InlineData("edit-source")]
    public async Task Delayed_extraction_cannot_write_after_delete_disable_restore_or_exit(string operation)
    {
        _provider.BlockExtraction = true;
        var result = await _chat.SendAsync("mash", "我喜欢茶", default, Project);
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        await _provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (operation == "delete") await new UserDataDeletionService(_database, _conversations, _repository, dialogueLifetime: _lifetime)
            .DeleteConversationAsync(result.ConversationId, "mash", default);
        if (operation == "disable") { _settings.Enabled = false; _repository.InvalidateWrites(); }
        if (operation == "restore") _repository.StartSession(); // Same persisted source IDs, new process generation.
        if (operation == "exit") await _queue.CancelAndDrainAsync(default);
        if (operation == "edit-source")
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE chat_messages SET text='已更正的原文' WHERE conversation_id=$id AND role='user'";
            command.Parameters.AddWithValue("$id", result.ConversationId);
            command.ExecuteNonQuery();
        }
        _provider.Release.TrySetResult();
        if (operation == "restore") Assert.Equal(MemoryStageStatus.Stale, (await _sink.Completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Status);
        await _queue.CancelAndDrainAsync(default);
        Assert.Empty(_repository.ListCandidates("mash"));
        Assert.Empty(_repository.ListMemories("mash"));
    }
    [Fact]
    public async Task Tool_fallback_rebuilds_memory_after_an_edit_between_primary_attempts()
    {
        await Send("我喜欢茶");
        var candidate = Assert.Single(_repository.ListCandidates("mash"));
        var memory = _repository.ReviewCandidate(candidate.CandidateId, "mash", MemoryReviewAction.Approve, null, DateTimeOffset.UtcNow)!;
        _settings.Save(_settings.Load() with { ModelConnection = _settings.Load().ModelConnection! with { ToolsSupported = true } });
        _provider.BeforeToolRejection = () => _repository.ReviewMemory(memory.MemoryId, "mash", MemoryReviewAction.Edit, "我不喜欢茶", DateTimeOffset.UtcNow);
        await Send("喜欢茶的偏好是什么");
        Assert.Contains(_provider.MainRequests[^2].Messages, m => m.Text.Contains("source=\"memory:") && m.Text.Contains("我喜欢茶"));
        Assert.Contains("我不喜欢茶", Assert.Single(MemoryBlocks()));
        Assert.DoesNotContain(MemoryBlocks(), text => text.Contains("我喜欢茶"));
    }
    [Theory]
    [InlineData("unsupported")]
    [InlineData("duplicate-key")]
    [InlineData("assistant-evidence")]
    [InlineData("too-many")]
    [InlineData("length")]
    public async Task Invalid_model_extraction_never_stages_or_approves_memory(string mode)
    {
        _provider.ExtractionMode = mode;
        await Send("我喜欢茶");
        Assert.Empty(_repository.ListCandidates("mash"));
        Assert.Empty(_repository.ListMemories("mash"));
        Assert.Single(_provider.Extractions);
    }
    [Theory]
    [InlineData("收到")]
    [InlineData("可以！")]
    [InlineData("对的")]
    public async Task Short_acknowledgement_does_not_turn_assistant_speculation_into_user_evidence_and_disabled_memory_stays_off(string text)
    {
        _provider.MainText = "我猜你喜欢咖啡。";
        await Send(text);
        Assert.Empty(_provider.Extractions);
        Assert.Empty(_repository.ListCandidates("mash"));
        _settings.Enabled = false;
        _repository.InvalidateWrites();
        await Send("我喜欢茶");
        Assert.Empty(_provider.Extractions);
        Assert.Empty(MemoryBlocks());
    }
    [Theory]
    [InlineData("修好了。", "short-without-context")]
    [InlineData("好的！！今天的午餐吃面。", "acknowledgement-evidence")]
    public async Task Unsupported_short_evidence_cannot_create_a_candidate(string text, string mode)
    {
        _provider.ExtractionMode = mode;
        await Send(text);
        Assert.Empty(_repository.ListCandidates("mash"));
    }

    private async Task<MemoryStageResult?> Send(string text, bool startNew = true)
    {
        if (startNew) _chat.StartNewConversation("mash");
        Assert.Equal(ConversationSendStatus.Completed, (await _chat.SendAsync("mash", text, default, Project)).Status);
        return _settings.Enabled ? await _sink.Completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) : null;
    }
    private string[] MemoryBlocks() => _provider.MainRequests.Last().Messages.Where(m => m.Text.Contains("source=\"memory:")).Select(m => m.Text).ToArray();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        _provider.Release.TrySetResult();
        await _queue.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
    private sealed class ObservingSink(SqliteMemoryRepository repository) : IMemoryCandidateSink
    {
        public Channel<MemoryStageResult> Completed = Channel.CreateUnbounded<MemoryStageResult>();
        public Action<IReadOnlyList<MemoryProposal>, MemoryStageResult>? Observed { get; init; }
        public MemoryWriteTicket Begin(MemorySource source) => repository.Begin(source);
        public MemoryWriteTicket? Refresh(MemoryWriteTicket ticket) => repository.Refresh(ticket);
        public MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals)
        { var result = repository.Stage(ticket, proposals); Observed?.Invoke(proposals, result); Completed.Writer.TryWrite(result); return result; }
        public void Abandon(MemoryWriteTicket ticket) => repository.Abandon(ticket);
    }
    private sealed class Settings : IDialogueSettingsStore, IMemorySettingsStore
    {
        private DialogueSettings _settings = DialogueSettings.Defaults with { ModelConnection = new("test", "https://fixture.test", "m", toolsSupported: false, contextWindowOverride: 16384) };
        public bool Enabled = true;
        public bool AutoRecord;
        public DialogueSettings Load() => _settings;
        public void Save(DialogueSettings value) => _settings = value;
        MemorySettings IMemorySettingsStore.Load() => new(Enabled, AutoRecord);
        public void Save(MemorySettings value) { Enabled = value.Enabled; AutoRecord = value.AutoRecordEpisodic; }
    }
    private sealed class Resolver(Provider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Content : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken token) => Task.FromResult(new ContentBinding(
            new("mash", "test", "1", "default", "1", "1"), new PersonaBundle("mash", "test", "1", "1", "你好", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "m";
        public string MainText = "收到，继续讨论。";
        public bool BlockExtraction;
        public string ExtractionMode = "";
        public Action? BeforeToolRejection;
        public List<ChatRequest> MainRequests = [];
        public List<ChatRequest> Extractions = [];
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            request.Metadata.TryGetValue("fgo_auxiliary", out var purpose);
            if (purpose == "memory_verification")
            {
                using var assessment = JsonDocument.Parse(request.Messages.Last().Text);
                var accepted = assessment.RootElement.GetProperty("user").GetString() == "总算搞定了";
                yield return new(accepted ? """{"approved":[true]}""" : """{"approved":[false]}""",
                    IsComplete: true, FinishReason: "stop");
                yield break;
            }
            if (purpose != "memory_extraction")
            {
                MainRequests.Add(request);
                if (BeforeToolRejection is { } action)
                { BeforeToolRejection = null; action(); throw new ProviderRequestException(ProviderFailureCategory.ToolsRejected, "fixture"); }
                yield return new(MainText, IsComplete: true, FinishReason: "stop"); yield break;
            }
            Extractions.Add(request);
            if (BlockExtraction) { Started.TrySetResult(); await Release.Task; }
            using var data = JsonDocument.Parse(request.Messages.Last().Text);
            var user = data.RootElement.GetProperty("user").GetString();
            var response = "{\"proposals\":[]}";
            if (user == "总算搞定了") response = """
                {"proposals":[{"text":"用户修好了月灯的导出按钮。","evidence":"总算搞定了","supporting_evidence":"我正在修月灯的导出按钮","content_type":"Episodic","operation":"Create"}]}
                """;
            if (user == "假设月灯的导出按钮已经发布。") response = """
                {"proposals":[{"text":"月灯的导出按钮已发布。","evidence":"月灯的导出按钮已经发布","content_type":"Episodic","operation":"Create"}]}
                """;
            if (user == "我喜欢茶") response = "{\"proposals\":[{\"text\":\"我喜欢茶\",\"evidence\":\"我喜欢茶\"}]}";
            if (user == "导出功能昨天进入验收。")
                response = """{"proposals":[{"text":"昨天（相对于用户陈述时间），导出功能进入验收。","evidence":"导出功能昨天进入验收"}]}""";
            if (user == "修好了" && data.RootElement.TryGetProperty("recent_context_only", out var context) &&
                context.EnumerateArray().Any(turn => turn.GetProperty("assistant").GetString() == "导出功能修好了吗？") &&
                data.RootElement.TryGetProperty("source_at_utc", out var sourceAt) && sourceAt.TryGetDateTimeOffset(out _))
                response = """{"proposals":[{"text":"截至用户陈述时，导出功能已修复。","evidence":"修好了"},{"text":"截至用户陈述时，导出功能已修复。","evidence":"修好了"}]}""";
            if (user == "我不再喜欢茶，改为偏好咖啡")
            {
                var original = data.RootElement.GetProperty("confirmed").EnumerateArray().First();
                response = JsonSerializer.Serialize(new { proposals = new[] { new { text = "我偏好咖啡", evidence = "我不再喜欢茶，改为偏好咖啡",
                    replaces_memory_id = original.GetProperty("id").GetString(), expected_version = original.GetProperty("version").GetInt32() } } });
            }
            response = ExtractionMode switch
            {
                "unsupported" => "{\"proposals\":[{\"text\":\"我喜欢茶\",\"evidence\":\"我喜欢茶\",\"status\":\"approved\"}]}",
                "duplicate-key" => "{\"proposals\":[],\"proposals\":[]}",
                "assistant-evidence" => "{\"proposals\":[{\"text\":\"喜欢咖啡\",\"evidence\":\"猜你喜欢咖啡\"}]}",
                "short-without-context" => """{"proposals":[{"text":"导出功能已修复","evidence":"修好了。"}]}""",
                "acknowledgement-evidence" => """{"proposals":[{"text":"喜欢咖啡","evidence":"好的！！"}]}""",
                "previous-turn-evidence" => """{"proposals":[{"text":"导出功能已验收","evidence":"导出功能昨天进入验收"}]}""",
                "too-many" => "{\"proposals\":[{},{},{},{}]}",
                _ => response
            };
            yield return new(response, IsComplete: true, FinishReason: ExtractionMode == "length" ? "length" : "stop");
        }
    }
}
