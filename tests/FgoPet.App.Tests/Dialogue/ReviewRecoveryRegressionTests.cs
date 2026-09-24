using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Core.Todo;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

/// <summary>Real SQLite projections and the public send path, not just planner/fake-store success.</summary>
public sealed class ReviewRecoveryRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tool_downgrade_then_context_recovery_succeeds_even_if_saving_capability_fails(bool saveFails)
    {
        using var fixture = new Fixture(saveFails);
        fixture.AddExchanges(5);
        var provider = new ScriptedProvider([ProviderFailureCategory.ToolsRejected, ProviderFailureCategory.ContextLimitExceeded]);
        var result = await fixture.Orchestrator(provider).SendAsync("mash", "continue", default);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(new[] { true, false, false }, provider.Requests.Select(r => r.Tools is { Count: > 0 }).ToArray());
        Assert.Single(fixture.Summarizer.Requests);
        Assert.NotNull(fixture.Store.Read(Scope, ConversationId).Summary);
        Assert.Equal(saveFails, fixture.Settings.Load().ModelConnection!.ToolsSupported);
        Assert.All(provider.Requests.Skip(1), request =>
        {
            Assert.Contains("\"todos\":[", request.Messages[2].Text);
            Assert.DoesNotContain("请调用 submit_todo_proposals", request.Messages[2].Text);
        });
    }

    [Fact]
    public async Task Context_recovery_then_tool_downgrade_also_succeeds()
    {
        using var fixture = new Fixture();
        fixture.AddExchanges(5);
        var provider = new ScriptedProvider([ProviderFailureCategory.ContextLimitExceeded, ProviderFailureCategory.ToolsRejected]);
        var result = await fixture.Orchestrator(provider).SendAsync("mash", "continue", default);

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(new[] { true, true, false }, provider.Requests.Select(r => r.Tools is { Count: > 0 }).ToArray());
        Assert.Single(fixture.Summarizer.Requests);
    }

    [Fact]
    public async Task Combined_recoveries_never_issue_a_fourth_primary_request()
    {
        using var fixture = new Fixture();
        fixture.AddExchanges(5);
        var provider = new ScriptedProvider([ProviderFailureCategory.ToolsRejected,
            ProviderFailureCategory.ContextLimitExceeded, ProviderFailureCategory.ContextLimitExceeded]);
        var result = await fixture.Orchestrator(provider).SendAsync("mash", "continue", default);

        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Single(fixture.Summarizer.Requests);
    }

    [Theory]
    [InlineData("text", ProviderFailureCategory.ContextLimitExceeded)]
    [InlineData("reasoning", ProviderFailureCategory.ContextLimitExceeded)]
    [InlineData("tool", ProviderFailureCategory.ContextLimitExceeded)]
    [InlineData("text", ProviderFailureCategory.ToolsRejected)]
    [InlineData("reasoning", ProviderFailureCategory.ToolsRejected)]
    [InlineData("tool", ProviderFailureCategory.ToolsRejected)]
    public async Task Any_output_prevents_automatic_replay(string kind, ProviderFailureCategory failure)
    {
        using var fixture = new Fixture();
        fixture.AddExchanges(5);
        var provider = new ScriptedProvider([failure]) { PartialOutput = kind };
        var result = await fixture.Orchestrator(provider).SendAsync("mash", "continue", default);

        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Single(provider.Requests);
        Assert.Empty(fixture.Summarizer.Requests);
        Assert.Null(fixture.Store.Read(Scope, ConversationId).Summary);
        Assert.Equal(ChatMessageStatus.Failed, fixture.Repository.LoadMessages(ConversationId, "mash")[^1].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_failed_or_cancelled_send_does_not_permanently_block_later_compaction(bool cancel)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(cancel ? [] : [ProviderFailureCategory.ServiceUnavailable])
        {
            SuccessText = new string('a', 1800),
            OnFirstRequest = cancel ? () => cancellation.Cancel() : null,
        };
        var orchestrator = fixture.Orchestrator(provider);
        var first = await orchestrator.SendAsync("mash", new string('u', 1800), cancellation.Token);
        Assert.Equal(cancel ? ConversationSendStatus.Cancelled : ConversationSendStatus.Failed, first.Status);
        var firstAttempt = fixture.Repository.LoadMessages(ConversationId, "mash").ToArray();
        Assert.Equal(cancel ? 1 : 2, firstAttempt.Length);

        for (var turn = 0; turn < 5; turn++)
            Assert.Equal(ConversationSendStatus.Completed,
                (await orchestrator.SendAsync("mash", new string('u', 1800), default)).Status);
        var before = fixture.Repository.LoadMessages(ConversationId, "mash").ToArray();
        Assert.True(await fixture.Compact());
        var after = fixture.Repository.LoadMessages(ConversationId, "mash").ToArray();
        Assert.Equal(before, after);
        Assert.Equal(firstAttempt, after.Take(firstAttempt.Length).ToArray());
        var projection = fixture.Store.Read(Scope, ConversationId);
        Assert.NotNull(projection.Summary);
        Assert.True(projection.Summary!.CoveredThroughSequence >= firstAttempt.Length);
        Assert.True(projection.UncoveredMessages.Count(m => m.Role == ChatMessageRole.Assistant &&
            m.Status == ChatMessageStatus.Completed) >= 2);
        Assert.Contains(fixture.Summarizer.Requests.SelectMany(r => r.Messages), m =>
            m.Text.Contains(firstAttempt[0].MessageId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("user-only")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Store_covers_a_settled_attempt_but_not_an_active_tail(string kind)
    {
        using var fixture = new Fixture();
        fixture.Append(ChatMessageRole.User, ChatMessageStatus.Completed, "The unanswered request is still evidence.");
        if (kind != "user-only") fixture.Append(ChatMessageRole.Assistant,
            kind == "failed" ? ChatMessageStatus.Failed : ChatMessageStatus.Cancelled, "");
        var active = fixture.Store.Read(Scope, ConversationId);
        var end = active.UncoveredMessages[^1];
        Assert.False(fixture.Store.TryCommit(Commit(active, end)));

        fixture.Append(ChatMessageRole.User, ChatMessageStatus.Completed, "A new request closes the old attempt.");
        var settled = fixture.Store.Read(Scope, ConversationId);
        var raw = fixture.Repository.LoadMessages(ConversationId, "mash").ToArray();
        Assert.False(fixture.Store.TryCommit(Commit(active, end))); // stale snapshot still rejected
        Assert.True(fixture.Store.TryCommit(Commit(settled, end)));
        Assert.Equal(raw, fixture.Repository.LoadMessages(ConversationId, "mash").ToArray());
        Assert.Single(fixture.Store.Read(Scope, ConversationId).UncoveredMessages);
    }

    [Fact]
    public async Task Pending_prefix_and_partial_exchange_boundary_remain_ineligible()
    {
        using var fixture = new Fixture();
        fixture.Append(ChatMessageRole.User, ChatMessageStatus.Pending, "Still in flight.");
        fixture.Append(ChatMessageRole.Assistant, ChatMessageStatus.Completed, "A reply does not settle pending state.");
        fixture.AddExchanges(5);
        var source = fixture.Store.Read(Scope, ConversationId);
        Assert.False(fixture.Store.TryCommit(Commit(source, source.UncoveredMessages[1])));
        Assert.False(await fixture.Compact());
        Assert.Empty(fixture.Summarizer.Requests);

        using var complete = new Fixture();
        complete.AddExchanges(3);
        var exchanges = complete.Store.Read(Scope, ConversationId);
        Assert.False(complete.Store.TryCommit(Commit(exchanges, exchanges.UncoveredMessages[0])));
    }

    [Fact]
    public void Interrupted_attempts_do_not_replace_recent_successful_exchanges()
    {
        CompactionTurn[] turns = [new(1, 2, 100, true), new(3, 4, 100, true), new(5, 6, 100, true),
            new(7, 8, 100, true, HasCompletedReply: false), new(9, 9, 100, false, HasCompletedReply: false)];
        Assert.Equal(new CompactableRange(1, 2), CompactionPlanner.Select(turns, 0));
    }

    [Fact]
    public void Prompt_contract_matches_the_attached_tool_capability()
    {
        var composer = new PromptComposer();
        var context = new PromptContext(Key, Persona, [], [], "", [], "Plan a task.");
        var fallback = composer.Compose(context, Route, Budget);
        var instructions = fallback.Messages[2].Text;
        Assert.DoesNotContain("submit_todo_proposals", instructions);
        Assert.Contains("尚未创建", instructions);
        Assert.Contains("\"todos\":[", instructions);
        var sample = instructions.Split('\n').Single(line => line.Contains("文本提案 JSON：", StringComparison.Ordinal));
        sample = sample[(sample.IndexOf('：') + 1)..].Trim().TrimEnd('。');
        using var parsed = JsonDocument.Parse(sample);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.GetProperty("todos").ValueKind);
        Assert.True(parsed.RootElement.TryGetProperty("text", out _));
        Assert.True(parsed.RootElement.TryGetProperty("emotion", out _));
        Assert.True(parsed.RootElement.GetProperty("todos")[0].GetProperty("steps")[0].TryGetProperty("title", out _));
        var tools = composer.Compose(context, Route, Budget, [TodoToolContracts.CreateSubmitTodoProposals()], "auto");
        Assert.Contains("请调用 submit_todo_proposals", tools.Messages[2].Text);
        Assert.DoesNotContain("当前请求没有可调用的工具", tools.Messages[2].Text);
    }

    private const string ConversationId = "review-conversation";
    private static readonly ContentContextKey Key = new("mash", "test.pack", "1.0.0", "default", "1", "1");
    private static readonly PersonaBundle Persona = new("mash", "test.pack", "1.0.0", "1", "Stay helpful.", []);
    private static readonly ConversationScope Scope = new("mash", null);
    private static readonly ModelConnectionSettings Connection = new("test", "https://example.test/v1", "test-model", contextWindowOverride: 32768);
    private static readonly ModelRouteKey Route = ModelRouteKey.From(Connection);
    private static readonly PromptBudget Budget = PromptBudget.Resolve(new(Route, 32768, null, ContextLimitSource.Override, "fixture"), 2048);
    private static string Outline(string id) => "任务目标：保留用户请求\n用户已确认的决定：未确定\n否定、更正及被替代事项：未确定\n尚未解决的问题：旧请求未获答复\n下一步：继续\n来源消息：" + id;
    private static CompactionCommit Commit(ConversationContextSnapshot source, ChatMessage end) => new(source,
        new ConversationSummary("summary-" + Guid.NewGuid().ToString("N"), ConversationId, "mash", Outline(end.MessageId),
            end.Sequence, end.MessageId, Key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Route, 30000, 10000);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-review-recovery-" + Guid.NewGuid().ToString("N"));
        private readonly RuntimeDatabase _database;
        private readonly RequestTokenMeter _meter = new();
        private readonly DialogueContextLifetime _lifetime = new();
        public SqliteConversationRepository Repository { get; }
        public SqliteConversationContextStore Store { get; }
        public RecordingSummarizer Summarizer { get; } = new();
        public ReviewSettings Settings { get; }
        public Fixture(bool failSave = false)
        {
            Directory.CreateDirectory(_root);
            _database = TestRuntimeDatabase.Create(Path.Combine(_root, "runtime.db"));
            new RuntimeDatabaseMigrator(_database).Migrate();
            Repository = new(_database);
            Repository.CreateConversation(ConversationId, "mash", Key, DateTimeOffset.UtcNow);
            Store = new(_database);
            Settings = new(failSave);
        }
        public void Append(ChatMessageRole role, ChatMessageStatus status, string text)
        {
            var sequence = Repository.LoadMessages(ConversationId, "mash").Count + 1;
            Repository.Append(new("message-" + sequence, ConversationId, "mash", role, text, status,
                DateTimeOffset.UtcNow, Key, sequence));
        }
        public void AddExchanges(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Append(ChatMessageRole.User, ChatMessageStatus.Completed, new string('u', 1800));
                Append(ChatMessageRole.Assistant, ChatMessageStatus.Completed, new string('a', 1800));
            }
        }
        public ConversationOrchestrator Orchestrator(IChatProvider provider)
        {
            var orchestrator = new ConversationOrchestrator(new Resolver(provider), new ContentResolver(), Repository,
                new SqliteMemoryRepository(_database), new PromptComposer(_meter), TimeProvider.System, Settings,
                summaries: new ConversationSummaryService(Store, Summarizer, _meter, TimeProvider.System),
                tokenMeter: _meter, lifetime: _lifetime, contextStore: Store);
            orchestrator.LoadConversation(ConversationId, "mash");
            return orchestrator;
        }
        public async Task<bool> Compact()
        {
            var source = Store.Read(Scope, ConversationId);
            ComposedPrompt Compose(ConversationSummary? summary, IReadOnlyList<ChatMessage> tail) =>
                new PromptComposer(_meter).Compose(new PromptContext(Key, Persona, [], [], "",
                    tail.Where(m => m.Status == ChatMessageStatus.Completed).Select(m => new PromptMessage(m.Role, m.Text)).ToArray(),
                    "continue", conversationSummary: summary), Route, Budget);
            using var lease = _lifetime.Acquire(default);
            return await new ConversationSummaryService(Store, Summarizer, _meter, TimeProvider.System).TryCompactAsync(
                source, new ScriptedProvider([]), Route, Budget, Compose(source.Summary, source.UncoveredMessages).Usage.InputTokens,
                (summary, tail) => Compose(summary, tail), lease, () => { }, new CompactionCallBudget(), default);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class ReviewSettings(bool failSave) : IDialogueSettingsStore
    {
        private DialogueSettings _current = DialogueSettings.Defaults with { ModelConnection = Connection };
        public DialogueSettings Load() => _current;
        public void Save(DialogueSettings settings)
        {
            if (failSave) throw new IOException("Fixture capability persistence failure.");
            _current = settings;
        }
    }
    private sealed class Resolver(IChatProvider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }
    private sealed class ContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(Key, Persona, [], [], "", ""));
    }
    private sealed class RecordingSummarizer : IConversationSummarizer
    {
        public List<ChatRequest> Requests { get; } = [];
        public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            const string marker = "完整ID：";
            var instruction = request.Messages[0].Text;
            var id = instruction[(instruction.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            return Task.FromResult(new SummaryAttempt(Outline(id), "stop", null));
        }
    }
    private sealed class ScriptedProvider(ProviderFailureCategory[] failures) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "test-model";
        public List<ChatRequest> Requests { get; } = [];
        public string? PartialOutput { get; init; }
        public Action? OnFirstRequest { get; init; }
        public string SuccessText { get; init; } = "已恢复，尚未创建待办。";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([]);
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            await Task.Yield();
            if (index == 0) OnFirstRequest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (index < failures.Length)
            {
                if (PartialOutput is { } kind)
                    yield return kind switch
                    {
                        "text" => new ChatStreamChunk("partial"),
                        "reasoning" => new ChatStreamChunk("", ReasoningDelta: "partial"),
                        _ => new ChatStreamChunk("", ToolCallDelta: new ChatToolCallDelta(0, id: "call-review", name: "submit_todo_proposals")),
                    };
                throw new ProviderRequestException(failures[index], "Fixture provider rejection.");
            }
            yield return new ChatStreamChunk(SuccessText, IsComplete: true, FinishReason: "stop");
        }
    }
}
