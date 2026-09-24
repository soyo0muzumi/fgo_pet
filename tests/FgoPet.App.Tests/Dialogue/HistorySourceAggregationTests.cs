using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class HistorySourceAggregationTests
{
    private static readonly ConversationScope Scope = new("mash", "project-a");
    private static readonly HistoryAnchor Anchor = new("history", "message-1", 1, DateTimeOffset.UnixEpoch);
    private static HistoryHit Hit(string text, bool truncated = false) => new(Anchor, "历史会话", text, truncated);
    private static HistoryReadCursor Next(int offset) => new(Scope.Key, Anchor.ConversationId, Anchor.Sequence, offset);

    [Fact]
    public async Task Consecutive_fragments_keep_the_later_correction_and_one_prompt_source()
    {
        const string first = "原计划：先讲信号项目。🙂";
        const string correction = "更正：改为先讲无线实验。";
        var repository = new ScriptedRepository([
            new([Hit(first, true)], Next(first.Length)),
            new([Hit(correction)], null),
        ]);
        var result = await Retrieve(repository);
        Assert.Equal(RecallStatus.Found, result.Status);
        var source = Assert.Single(result.Sources);
        Assert.Equal(first + correction, source.Excerpt);
        Assert.Equal(Anchor, source.Anchor);
        Assert.False(source.IsTruncated);
        Assert.Equal(first.Length, repository.Cursors[1]!.TextOffset);

        var key = new ContentContextKey("mash", "pack", "1", "default", "1", "1");
        var route = new ModelRouteKey("test", "endpoint", "m", "1");
        var budget = PromptBudget.Resolve(new(route, 16384, null, ContextLimitSource.Override, "test"), 2048);
        var prompt = new PromptComposer().Compose(new(key, new PersonaBundle("mash", "pack", "1", "1", "认真回应。", []),
            [], [], "", [], "继续", recalledHistory: result.Sources, recallStatus: result.Status), route, budget);
        var citation = Assert.Single(prompt.Messages.Where(message => message.Text.Contains("source=\"recalled:")));
        Assert.Contains(first + correction, citation.Text);
    }

    [Fact]
    public async Task Repeated_words_at_different_offsets_are_not_deduplicated_as_overlap()
    {
        var result = await Retrieve(new ScriptedRepository([
            new([Hit("哈哈", true)], Next(2)), new([Hit("哈哈")], null),
        ]));
        Assert.Equal("哈哈哈哈", Assert.Single(result.Sources).Excerpt);
    }

    [Fact]
    public async Task Consistent_overlap_is_included_once()
    {
        var result = await Retrieve(new ScriptedRepository([
            new([Hit("abcdef", true)], Next(3)), new([Hit("defXYZ")], null),
        ]));
        Assert.Equal("abcdefXYZ", Assert.Single(result.Sources).Excerpt);
    }

    [Fact]
    public async Task Exact_duplicates_share_a_card_but_distinct_message_ids_remain_distinct()
    {
        var first = Hit("相同的文字");
        var second = first with { Anchor = new("history", "message-2", 2, DateTimeOffset.UnixEpoch.AddSeconds(1)) };
        var result = await Retrieve(new ScriptedRepository([new([first, first, second], null)]));
        Assert.Equal(RecallStatus.Found, result.Status);
        Assert.Equal(new[] { first, second }, result.Sources);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("anchor")]
    [InlineData("title")]
    [InlineData("end")]
    [InlineData("scope")]
    public async Task Inconsistent_pages_never_become_confident_sources(string failure)
    {
        var offset = failure == "gap" ? 4 : failure == "overlap" ? 1 : 3;
        var second = Hit(failure == "overlap" ? "XX" : "def");
        if (failure == "anchor") second = second with { Anchor = Anchor with { CreatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1) } };
        if (failure == "title") second = second with { Title = "另一版本" };
        if (failure == "scope") second = second with { Anchor = Anchor with { ConversationId = "another" } };
        var result = await Retrieve(new ScriptedRepository([
            new([Hit("abc", failure != "end")], Next(offset)), new([second], null),
        ]));
        Assert.Equal(RecallStatus.Unavailable, result.Status);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Total_character_and_three_read_limits_remain_in_force()
    {
        var chunk = new string('x', 2000);
        var repository = new ScriptedRepository([
            new([Hit(chunk, true)], Next(2000)),
            new([Hit(chunk, true)], Next(4000)),
            new([Hit(chunk, true)], Next(6000)),
            new([Hit("must-not-read")], null),
        ]);
        var result = await Retrieve(repository);
        Assert.Equal(new[] { 6000, 4000, 2000 }, repository.Budgets);
        var source = Assert.Single(result.Sources);
        Assert.Equal(new string('x', 6000), source.Excerpt);
        Assert.True(source.IsTruncated);
    }

    [Fact]
    public async Task Cancellation_is_propagated_instead_of_returning_partial_sources()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new ScriptedRepository([new([Hit("abc", true)], Next(3))], cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Retrieve(repository, cancellation.Token));
    }

    [Fact]
    public async Task Sqlite_pages_aggregate_without_changing_raw_history_or_source_validation()
    {
        var path = Path.Combine(Path.GetTempPath(), "fgo-recall-aggregate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var database = TestRuntimeDatabase.Create(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var conversations = new SqliteConversationRepository(database);
            var key = new ContentContextKey("mash", "pack", "1", "default", "1", "1");
            conversations.CreateConversation("history", "mash", key, DateTimeOffset.UnixEpoch, Scope.ProjectId);
            var text = string.Concat(Enumerable.Repeat("原计划🙂；更正保留。", 400));
            conversations.Append(new("message-1", "history", "mash", ChatMessageRole.User, text,
                ChatMessageStatus.Completed, DateTimeOffset.UnixEpoch, key, 1));
            var repository = new SmallSqlitePages(new SqliteConversationRecallRepository(database));
            var result = await Retrieve(repository);
            Assert.Equal(RecallStatus.Found, result.Status);
            var source = Assert.Single(result.Sources);
            Assert.True(text.StartsWith(source.Excerpt, StringComparison.Ordinal));
            Assert.True(source.Excerpt.Length > 1024);
            Assert.InRange(source.Excerpt.Length, 3000, 3072);
            Assert.True(source.IsTruncated);
            Assert.True(conversations.IsCurrentSource(Scope, source));
            Assert.False(conversations.IsCurrentSource(new("mash", "other-project"), source));
            Assert.False(conversations.IsCurrentSource(new("other-servant", Scope.ProjectId), source));
            Assert.Equal(text, Assert.Single(conversations.LoadMessages("history", "mash")).Text);
            conversations.DeleteConversation("history", "mash");
            Assert.False(conversations.IsCurrentSource(Scope, source));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private static Task<ConversationRecallResult> Retrieve(IConversationRecallRepository repository, CancellationToken token = default)
    {
        var resolver = new NoModelResolver();
        var service = new ConversationRecallService(repository, resolver, new Settings(),
            new ModelContextResolver(_ => throw new InvalidOperationException("Direct matches must not query models.")), new RequestTokenMeter());
        return service.RetrieveAsync(Scope, "current", "原计划", token);
    }
    private sealed class NoModelResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() => throw new InvalidOperationException("Direct matches must not query models.");
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults;
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }
    private sealed class ScriptedRepository(IReadOnlyList<HistoryReadPage> pages, Action? afterRead = null) : IConversationRecallRepository
    {
        public List<int> Budgets { get; } = [];
        public List<HistoryReadCursor?> Cursors { get; } = [];
        public IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10) => [];
        public IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId, string query, int limit = 20) => [Hit("预览")];
        public HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000, HistoryReadCursor? cursor = null)
        {
            Assert.Equal(Scope, scope);
            Assert.Equal(Anchor, anchor);
            Cursors.Add(cursor);
            Budgets.Add(maxChars);
            var page = pages[Budgets.Count - 1];
            afterRead?.Invoke();
            return page;
        }
    }
    private sealed class SmallSqlitePages(SqliteConversationRecallRepository inner) : IConversationRecallRepository
    {
        public IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10) => inner.Browse(scope, excludedConversationId, limit);
        public IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId, string query, int limit = 20) => inner.Discover(scope, excludedConversationId, query, limit);
        public HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000, HistoryReadCursor? cursor = null) =>
            inner.Read(scope, anchor, Math.Min(maxChars, 1024), cursor);
    }
}
