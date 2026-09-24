using System.IO;
using System.Text.RegularExpressions;
using FgoPet.App.Dialogue;
using FgoPet.App.Privacy;
using FgoPet.App.Services;
using FgoPet.Core.Todo;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Memory.Settings;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed class ConversationCompactionEndToEndTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-compaction-e2e-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly DialogueContextLifetime _lifetime = new();
    private readonly Settings _settings = new();
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    private static readonly ConversationScope Scope = new("mash", null);
    public ConversationCompactionEndToEndTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
        _conversations.CreateConversation("c", "mash", Key, DateTimeOffset.UtcNow);
        _conversations.WriteState("LastActiveConversationId:mash", "c", DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Compaction_survives_restart_with_recent_exchanges_once_even_when_long_term_memory_is_disabled()
    {
        // Hermes kept_exchanges / seam_once regressions, adapted to a SQLite projection.
        Seed(16, 1100);
        var provider = new Provider();
        var result = await Create(provider).SendAsync("mash", "请继续", default);
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.InRange(provider.SummaryCalls, 1, 3);
        Assert.Single(provider.MainRequests);
        var sent = string.Join("\n", provider.MainRequests[0].Messages.Select(message => message.Text));
        Assert.Contains("conversation_summary", sent);
        Assert.DoesNotContain("raw-01", sent);
        Assert.Contains("raw-13", sent);
        Assert.Contains("raw-16", sent);
        Assert.All(provider.SummaryRequests, request => { Assert.Null(request.Tools); Assert.Equal(2048, request.MaxOutputTokens); });
        Assert.Equal(18, _conversations.LoadMessages("c", "mash").Count);
        var snapshot = new SqliteConversationContextStore(new RuntimeDatabase(_path, pooling: false)).Read(Scope, "c");
        Assert.NotNull(snapshot.Summary);
        Assert.Equal(6, snapshot.UncoveredMessages.Count);
        Assert.Equal(6, snapshot.UncoveredMessages.Select(message => message.MessageId).Distinct().Count());
        var restartedProvider = new Provider();
        var restarted = await Create(restartedProvider).SendAsync("mash", "再继续", default);
        Assert.Equal("c", restarted.ConversationId);
        Assert.Equal(0, restartedProvider.SummaryCalls);
        var next = string.Join("\n", Assert.Single(restartedProvider.MainRequests).Messages.Select(message => message.Text));
        Assert.Contains("conversation_summary", next);
        Assert.Single(Regex.Matches(next, "raw-13"));
        Assert.DoesNotContain("raw-01", next);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("switch")]
    [InlineData("model")]
    [InlineData("delete")]
    public async Task Late_summary_after_invalidation_cannot_commit(string operation)
    {
        Seed(16, 1100);
        var provider = new Provider { BlockSummary = true };
        var orchestrator = Create(provider);
        var sending = orchestrator.SendAsync("mash", "请继续", default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? deletion = null;
        if (operation == "cancel") orchestrator.CancelCurrent();
        if (operation == "switch") orchestrator.StartNewConversation("mash");
        if (operation == "model") _settings.Save(_settings.Load() with { ModelConnection = new("test", "https://other.test", "m",
            toolsSupported: false, contextWindowOverride: 32768) });
        if (operation == "delete") deletion = new UserDataDeletionService(_database, _conversations,
            new SqliteMemoryRepository(_database), dialogueLifetime: _lifetime).DeleteConversationAsync("c", "mash", default);
        provider.Release.TrySetResult();
        Assert.Equal(ConversationSendStatus.Cancelled, (await sending).Status);
        if (deletion is not null) await deletion.WaitAsync(TimeSpan.FromSeconds(5));
        using var db = _database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversation_contexts";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Empty(provider.MainRequests);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("large")]
    public async Task Failed_summary_preserves_raw_messages_and_never_sends_an_oversized_main_request(string mode)
    {
        Seed(16, 1100);
        var provider = new Provider { SummaryMode = mode };
        var result = await Create(provider).SendAsync("mash", "必须保留本次输入", default);
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Empty(provider.MainRequests);
        Assert.Null(new SqliteConversationContextStore(_database).Read(Scope, "c").Summary);
        Assert.Contains(_conversations.LoadMessages("c", "mash"), message => message.Text == "必须保留本次输入");
    }

    [Fact]
    public async Task Too_many_summary_chunks_stop_after_three_without_partial_projection()
    {
        Seed(60, 1100);
        var provider = new Provider();
        Assert.Equal(ConversationSendStatus.Failed, (await Create(provider).SendAsync("mash", "继续", default)).Status);
        Assert.Equal(3, provider.SummaryCalls);
        Assert.Empty(provider.MainRequests);
        Assert.Null(new SqliteConversationContextStore(_database).Read(Scope, "c").Summary);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task Context_error_retries_only_after_shrink_and_before_any_output(bool emitBeforeError, int expectedCalls)
    {
        Seed(6, 700);
        var provider = new Provider { ContextError = true, EmitBeforeError = emitBeforeError };
        var result = await Create(provider).SendAsync("mash", "继续", default);
        Assert.Equal(expectedCalls, provider.MainRequests.Count);
        Assert.Equal(emitBeforeError ? ConversationSendStatus.Failed : ConversationSendStatus.Completed, result.Status);
        Assert.Equal(emitBeforeError ? 0 : 1, provider.SummaryCalls);
        if (!emitBeforeError)
            Assert.True(provider.MainRequests[1].Messages.Sum(message => message.Text.Length) < provider.MainRequests[0].Messages.Sum(message => message.Text.Length));
    }

    [Fact]
    public async Task Pending_draft_is_reinjected_after_compaction_and_confirmed_once_at_current_version()
    {
        Seed(16, 1100);
        var todos = new SqliteTodoRepository(_database);
        var work = new TodoProposalService(new TodoApplicationService(todos, TimeProvider.System)).Drafts;
        var old = work.Replace("c", "mash", [new TodoProposal("旧任务")]);
        var current = work.Replace("c", "mash", [new TodoProposal("完整保留的新任务", "只需整理资料")]);
        var provider = new Provider();
        var orchestrator = Create(provider, work);
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "先继续讨论", default)).Status);
        Assert.True(provider.SummaryCalls > 0);
        Assert.Contains(Assert.Single(provider.MainRequests).Messages, m => m.Text.Contains("pending_todo_draft") && m.Text.Contains("完整保留的新任务"));
        Assert.All(provider.SummaryRequests, r => Assert.DoesNotContain(r.Messages, m => m.Text.Contains("完整保留的新任务")));
        Assert.Equal(TodoDraftResultKind.Stale, work.Confirm("c", "mash", old.DraftId, old.Version, "old").Kind);
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "确认创建", default)).Status);
        Assert.Equal("完整保留的新任务", Assert.Single(todos.List()).Title);
        work.Confirm("c", "mash", current.DraftId, current.Version, "repeat");
        Assert.Single(todos.List());
    }

    [Theory]
    [InlineData(ProviderFailureCategory.Authentication)]
    [InlineData(ProviderFailureCategory.RateLimited)]
    [InlineData(ProviderFailureCategory.Network)]
    [InlineData(ProviderFailureCategory.InvalidResponse)]
    public async Task Other_provider_errors_do_not_trigger_compaction_or_retry(ProviderFailureCategory category)
    {
        Seed(6, 700);
        var provider = new Provider { Failure = category };
        await Create(provider).SendAsync("mash", "继续", default);
        Assert.Single(provider.MainRequests);
        Assert.Equal(0, provider.SummaryCalls);
    }
    [Fact]
    public async Task Tool_fallback_then_context_error_recovers_once_with_a_smaller_no_tool_request()
    {
        Seed(6, 700);
        _settings.Save(_settings.Load() with { ModelConnection = _settings.Load().ModelConnection! with { ToolsSupported = true } });
        var provider = new Provider { RejectToolsFirst = true, ContextError = true };
        var result = await Create(provider).SendAsync("mash", "继续", default);
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(3, provider.MainRequests.Count);
        Assert.NotNull(provider.MainRequests[0].Tools);
        Assert.Null(provider.MainRequests[1].Tools);
        Assert.Null(provider.MainRequests[2].Tools);
        Assert.Equal(1, provider.SummaryCalls);
        Assert.Null(Assert.Single(provider.SummaryRequests).Tools);
        Assert.True(provider.MainRequests[2].Messages.Sum(message => message.Text.Length) <
            provider.MainRequests[1].Messages.Sum(message => message.Text.Length));
        Assert.Contains(provider.MainRequests[2].Messages, message => message.Text.Contains("conversation_summary"));
        Assert.DoesNotContain(provider.MainRequests[2].Messages, message => message.Text.Contains("submit_todo_proposals"));
        Assert.Equal(8, _conversations.LoadMessages("c", "mash").Count);
    }

    private ConversationOrchestrator Create(Provider provider, ITodoDraftWorkflow? work = null)
    {
        var store = new SqliteConversationContextStore(new RuntimeDatabase(_path, pooling: false));
        var meter = new RequestTokenMeter();
        return new(new Resolver(provider), new Content(), _conversations, new SqliteMemoryRepository(_database),
            new PromptComposer(meter), TimeProvider.System, settings: _settings, memorySettings: new MemoryOff(),
            summaries: new ConversationSummaryService(store, new ProviderConversationSummarizer(), meter, TimeProvider.System),
            tokenMeter: meter, lifetime: _lifetime, contextStore: store, todoDrafts: work);
    }
    private void Seed(int count, int chars)
    {
        for (var i = 1; i <= count; i++) _conversations.Append(new("m" + i, "c", "mash",
            i % 2 == 1 ? ChatMessageRole.User : ChatMessageRole.Assistant, $"raw-{i:D2} " + new string('x', chars),
            ChatMessageStatus.Completed, DateTimeOffset.UtcNow, Key, i));
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        private DialogueSettings _value = DialogueSettings.Defaults with { ModelConnection = new("test", "https://fixture.test", "m",
            toolsSupported: false, contextWindowOverride: 16384) };
        public DialogueSettings Load() => _value;
        public void Save(DialogueSettings value) => _value = value;
    }
    private sealed class MemoryOff : IMemorySettingsStore
    {
        public MemorySettings Load() => new(false);
        public void Save(MemorySettings settings) => throw new NotSupportedException();
    }
    private sealed class Resolver(Provider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Content : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(
            new ContentBinding(Key, new PersonaBundle("mash", "test", "1", "1", "你好", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "m";
        public List<ChatRequest> MainRequests { get; } = [];
        public List<ChatRequest> SummaryRequests { get; } = [];
        public int SummaryCalls => SummaryRequests.Count;
        public bool BlockSummary { get; init; }
        public bool ContextError { get; init; }
        public bool RejectToolsFirst { get; init; }
        public ProviderFailureCategory? Failure { get; init; }
        public bool EmitBeforeError { get; init; }
        public string SummaryMode { get; init; } = "";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.Messages[0].Text.StartsWith("根据提供"))
            {
                SummaryRequests.Add(request);
                if (BlockSummary) { Started.TrySetResult(); await Release.Task; }
                var lastId = Regex.Match(request.Messages.Last().Text, "source:([^:]+):").Groups[1].Value;
                if (SummaryMode == "large")
                {
                    yield return new(new string('x', 4000));
                    yield return new(new string('x', 3000), IsComplete: true, FinishReason: "stop");
                }
                else yield return new("任务目标：继续讨论\n用户已确认的决定：未确定\n否定、更正及被替代事项：未确定\n尚未解决的问题：未确定\n下一步：继续\n来源消息：" + lastId,
                    IsComplete: true, FinishReason: SummaryMode == "length" ? "length" : "stop");
                yield break;
            }
            MainRequests.Add(request);
            if (Failure is { } category) throw new ProviderRequestException(category, "fixture");
            if (RejectToolsFirst && MainRequests.Count == 1) throw new ProviderRequestException(ProviderFailureCategory.ToolsRejected, "fixture");
            if (ContextError && MainRequests.Count == (RejectToolsFirst ? 2 : 1))
            {
                if (EmitBeforeError) yield return new("partial");
                throw new ProviderRequestException(ProviderFailureCategory.ContextLimitExceeded, "fixture");
            }
            yield return new("好的", IsComplete: true, FinishReason: "stop");
        }
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }
}
