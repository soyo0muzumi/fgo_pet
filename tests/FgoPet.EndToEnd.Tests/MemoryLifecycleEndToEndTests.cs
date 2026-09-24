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

public sealed class MemoryLifecycleEndToEndTests : IAsyncLifetime
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
        _repository = new(_database); _repository.StartSession();
        _conversations = new(_database); _sink = new(_repository); _review = new(_repository, TimeProvider.System);
        var meter = new RequestTokenMeter();
        var limit = new ModelContextResolver(_ => _provider, TimeProvider.System);
        var recall = new MemoryRecallService(_repository);
        _queue = new(new ProviderMemoryCandidateExtractor(_ => _provider, limit, meter, recall), _sink, _repository, _lifetime,
            connection => _settings.Enabled && connection == _settings.Load().ModelConnection,
            isSourceCurrent: source => new MemoryExtractionSourceReader(_conversations).IsCurrent(source));
        _chat = new(new Resolver(_provider), new Content(), _conversations, recall, new(meter), TimeProvider.System,
            settings: _settings, memorySettings: _settings, contextResolver: limit, tokenMeter: meter, lifetime: _lifetime,
            memoryCandidates: _sink, memoryExtractions: _queue);
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
    [Fact]
    public async Task Short_acknowledgement_does_not_turn_assistant_speculation_into_user_evidence_and_disabled_memory_stays_off()
    {
        _provider.MainText = "我猜你喜欢咖啡。";
        await Send("收到");
        Assert.Empty(_provider.Extractions);
        Assert.Empty(_repository.ListCandidates("mash"));
        _settings.Enabled = false;
        _repository.InvalidateWrites();
        await Send("我喜欢茶");
        Assert.Empty(_provider.Extractions);
        Assert.Empty(MemoryBlocks());
    }
    private async Task Send(string text)
    {
        _chat.StartNewConversation("mash");
        Assert.Equal(ConversationSendStatus.Completed, (await _chat.SendAsync("mash", text, default, Project)).Status);
        if (_settings.Enabled) await _sink.Completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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
        public MemoryWriteTicket Begin(MemorySource source) => repository.Begin(source);
        public MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals)
        { var result = repository.Stage(ticket, proposals); Completed.Writer.TryWrite(result); return result; }
        public void Abandon(MemoryWriteTicket ticket) => repository.Abandon(ticket);
    }
    private sealed class Settings : IDialogueSettingsStore, IMemorySettingsStore
    {
        private DialogueSettings _settings = DialogueSettings.Defaults with { ModelConnection = new("test", "https://fixture.test", "m", toolsSupported: false, contextWindowOverride: 16384) };
        public bool Enabled = true;
        public DialogueSettings Load() => _settings;
        public void Save(DialogueSettings value) => _settings = value;
        MemorySettings IMemorySettingsStore.Load() => new(Enabled);
        public void Save(MemorySettings value) => Enabled = value.Enabled;
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
            if (!request.Messages[0].Text.StartsWith("提取长期记忆候选"))
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
            if (user == "我喜欢茶") response = "{\"proposals\":[{\"text\":\"我喜欢茶\",\"evidence\":\"我喜欢茶\"}]}";
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
                "too-many" => "{\"proposals\":[{},{},{},{}]}",
                _ => response
            };
            yield return new(response, IsComplete: true, FinishReason: ExtractionMode == "length" ? "length" : "stop");
        }
    }
}
