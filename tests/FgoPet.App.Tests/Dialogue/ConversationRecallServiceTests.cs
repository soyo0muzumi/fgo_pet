using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationRecallServiceTests
{
    [Fact]
    public async Task Model_keyword_groups_are_searched_as_bounded_literal_terms()
    {
        var repository = new Repository();
        var provider = new Provider("""{"queries":["导师 面谈 准备","其他 多余 检索"],"selected":[],"ambiguous":false}""");
        var result = await Create(repository, provider).RetrieveAsync(new("mash", "a"), "current", "继续昨天的准备", default);
        Assert.Equal(RecallStatus.Found, result.Status);
        Assert.Contains(result.Sources, hit => hit.Excerpt.Contains("改为先讲无线实验"));
        Assert.InRange(repository.Scopes.Count, 1, 4); // Original query plus at most three model-derived terms.
        Assert.All(repository.Scopes, scope => Assert.Equal("a", scope.ProjectId));
    }

    [Fact]
    public async Task Rewrite_finds_same_scope_original_and_later_correction_without_tools()
    {
        var repository = new Repository();
        var provider = new Provider("""{"queries":["面谈"],"selected":[],"ambiguous":false}""");
        var result = await Create(repository, provider).RetrieveAsync(new("mash", "a"), "current", "继续昨天的准备", default);
        Assert.Equal(RecallStatus.Found, result.Status);
        Assert.Contains(result.Sources, hit => hit.Excerpt.Contains("改为先讲无线实验"));
        Assert.Null(provider.Request!.Tools);
        Assert.Equal(512, provider.Request.MaxOutputTokens);
        Assert.Equal("history_recall", provider.Request.Metadata["fgo_auxiliary"]);
        Assert.Equal(1, provider.Calls);
        Assert.All(repository.Scopes, scope => Assert.Equal("a", scope.ProjectId));
        Assert.All(repository.Excluded, id => Assert.Equal("current", id));
    }

    [Theory]
    [InlineData("""{"queries":[],"selected":[99],"ambiguous":false}""", RecallStatus.Unavailable)]
    [InlineData("""{"queries":[],"selected":[0,1],"ambiguous":true}""", RecallStatus.Ambiguous)]
    [InlineData("""{"conversation_id":"invented","queries":[],"selected":[0],"ambiguous":false}""", RecallStatus.Unavailable)]
    public async Task Ambiguous_or_fabricated_selection_never_becomes_a_confident_source(string json, RecallStatus status)
    {
        var result = await Create(new Repository(), new Provider(json)).RetrieveAsync(new("mash", "a"), "current", "接着上次的计划", default);
        Assert.Equal(status, result.Status);
        Assert.Empty(result.Sources);
    }

    private static ConversationRecallService Create(Repository repository, Provider provider)
    {
        var settings = new Settings();
        return new(repository, new Resolver(provider), settings, new ModelContextResolver(_ => provider), new RequestTokenMeter());
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults with { ModelConnection = new("test", "https://fixture.test", "same", contextWindowOverride: 32768) };
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }
    private sealed class Resolver(Provider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Provider(string json) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "same";
        public int Calls { get; private set; }
        public ChatRequest? Request { get; private set; }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            await Task.Yield();
            yield return new(json, IsComplete: true, FinishReason: "stop");
        }
    }
    private sealed class Repository : IConversationRecallRepository
    {
        public List<ConversationScope> Scopes { get; } = [];
        public List<string> Excluded { get; } = [];
        private static HistoryHit Hit(string id, string title, string text) => new(new(id, id + "-m", 1, DateTimeOffset.UnixEpoch), title, text, false);
        public IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10) =>
            [Hit("interview", "导师面谈", "先讲信号项目"), Hit("travel", "旅游计划", "周五出发")];
        public IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId, string query, int limit = 20)
        {
            Scopes.Add(scope); Excluded.Add(excludedConversationId);
            return query == "面谈" ? [Hit("interview", "导师面谈", "先讲信号项目")] : [];
        }
        public HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000, HistoryReadCursor? cursor = null) =>
            new([Hit(anchor.ConversationId, "导师面谈", "先讲信号项目；后来改为先讲无线实验")], null);
    }
}
