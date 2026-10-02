using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationCompactionServiceTests
{
    [Fact]
    public async Task Summary_uses_raw_sources_and_commits_only_a_smaller_complete_prompt()
    {
        var store = new Store();
        var summaries = new Summarizer();
        var meter = new RequestTokenMeter();
        var service = new FgoPet.App.Dialogue.ConversationSummaryService(store, summaries, meter, TimeProvider.System);
        using var lease = new DialogueContextLifetime().Acquire(default);
        var result = await service.TryCompactAsync(store.Source, new Provider(), Route, Budget, 30000, Compose,
            lease, () => { }, new CompactionCallBudget(), default);
        Assert.True(result);
        Assert.NotNull(store.Commit);
        Assert.True(store.Commit!.InputTokensAfter < store.Commit.InputTokensBefore);
        Assert.All(summaries.Requests, request => Assert.Null(request.Tools));
        Assert.All(summaries.Requests, request => Assert.Equal("context_summary", request.Metadata["fgo_auxiliary"]));
        Assert.Contains("m6", store.Commit.Summary.SummaryText);
        Assert.DoesNotContain(summaries.Requests.SelectMany(request => request.Messages), message => message.Text.Contains("live-memory"));
        Assert.Equal(6, store.Commit.Summary.CoveredThroughSequence);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("empty")]
    [InlineData("unstructured")]
    [InlineData("wrong-source")]
    public async Task Invalid_summary_never_changes_active_projection(string mode)
    {
        var store = new Store();
        var service = new FgoPet.App.Dialogue.ConversationSummaryService(store, new Summarizer(mode), new RequestTokenMeter(), TimeProvider.System);
        using var lease = new DialogueContextLifetime().Acquire(default);
        Assert.False(await service.TryCompactAsync(store.Source, new Provider(), Route, Budget, 30000, Compose,
            lease, () => { }, new CompactionCallBudget(), default));
        Assert.Null(store.Commit);
    }

    private static readonly ModelRouteKey Route = new("test", "endpoint", "m", "1");
    [Fact]
    public async Task A_valid_outline_that_does_not_shrink_the_actual_prompt_cannot_commit()
    {
        var store = new Store();
        var service = new ConversationSummaryService(store, new Summarizer(), new RequestTokenMeter(), TimeProvider.System);
        using var lease = new DialogueContextLifetime().Acquire(default);
        Assert.False(await service.TryCompactAsync(store.Source, new Provider(), Route, Budget, 1, Compose,
            lease, () => { }, new CompactionCallBudget(), default));
        Assert.Null(store.Commit);
    }
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    private static readonly PromptBudget Budget = PromptBudget.Resolve(new(Route, 32768, null, ContextLimitSource.Override, "fixture"), 2048);
    private static ComposedPrompt Compose(ConversationSummary summary, IReadOnlyList<ChatMessage> tail) =>
        new PromptComposer().Compose(new PromptContext(Key, new PersonaBundle("mash", "test", "1", "1", "persona", []), [], [],
            "", tail.Select(message => new PromptMessage(message.Role, message.Text)).ToArray(), "continue", conversationSummary: summary),
            Route, Budget);

    private sealed class Store : IConversationContextStore
    {
        public ConversationContextSnapshot Source { get; } = new(new("mash", null), "c", 10, 0, null,
            Enumerable.Range(1, 10).Select(index => new ChatMessage("m" + index, "c", "mash",
                index % 2 == 1 ? ChatMessageRole.User : ChatMessageRole.Assistant, "raw " + new string('x', 1800),
                ChatMessageStatus.Completed, DateTimeOffset.UnixEpoch, Key, index)).ToArray(), "fixture");
        public CompactionCommit? Commit { get; private set; }
        public ConversationContextSnapshot Read(ConversationScope scope, string conversationId) => Source;
        public bool TryCommit(CompactionCommit commit) { Commit = commit; return true; }
    }
    private sealed class Summarizer(string mode = "") : IConversationSummarizer
    {
        public List<ChatRequest> Requests { get; } = [];
        public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new SummaryAttempt(mode == "empty" ? "" : mode == "unstructured" ? "lost structure" :
                "任务目标：fixture\n用户已确认的决定：未确定\n否定、更正及被替代事项：未确定\n尚未解决的问题：未确定\n下一步：继续\n来源消息：m1 m2 m3 m4 m5 " + (mode == "wrong-source" ? "m60" : "m6"),
                mode == "length" ? "length" : "stop", null));
        }
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "m";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
