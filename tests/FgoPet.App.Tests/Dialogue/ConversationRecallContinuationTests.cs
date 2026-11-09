using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

/// <summary>
/// Design stage 0 / E1 acceptance: continuation-aware retrieval.
///
/// These were written against the acceptance criteria in EXPERIENCE-RECALL-DESIGN.zh-CN.md E1:
///   - "终于解决了！" with no confirmable continuation cue produces no candidate
///   - small talk produces no candidate
///   - a real recent continuation cue produces candidates that pass source review
///   - a turn with no match stays Empty and injects nothing
///
/// These tests exercise the context-aware overload directly.
/// </summary>
[Collection(nameof(ContinuationSwitchTests))]
public sealed class ConversationRecallContinuationTests
{
    private static ConversationRecallService Create(IConversationRecallRepository repository, string json = "invalid") =>
        new(repository, new Resolver(new Provider(json)), new Settings(),
            new ModelContextResolver(_ => new Provider(json)), new RequestTokenMeter());

    [Theory]
    [InlineData("终于解决了！")]
    [InlineData("今天天气不错啊")]
    [InlineData("你好")]
    [InlineData("嗯")]
    public async Task Self_contained_turn_without_continuation_cue_produces_no_candidate(string message)
    {
        var repository = new Repository();
        var result = await Create(repository).RetrieveAsync(new("mash", "a"), "current", message,
            [], default);
        Assert.Equal(RecallStatus.Empty, result.Status);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Recent_turn_naming_the_subject_lets_a_bare_continuation_through()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fgo-recall-continuation-{Guid.NewGuid():N}.db");
        try
        {
            var database = TestRuntimeDatabase.Create(path);
            new RuntimeDatabaseMigrator(database).Migrate();
            var conversations = new SqliteConversationRepository(database);
            var binding = new ContentContextKey("mash", "pack", "1", "default", "1", "1");
            var time = DateTimeOffset.UnixEpoch;
            var scope = new ConversationScope("mash", "a");
            conversations.CreateConversation("history", "mash", binding, time, scope.ProjectId);
            conversations.Append(new("history-message", "history", "mash", ChatMessageRole.User,
                "说话动作的过渡时间问题终于解决了！", ChatMessageStatus.Completed, time, binding, 1));

            var recall = Create(new SqliteConversationRecallRepository(database));
            var result = await recall.RetrieveAsync(scope, "current", "终于解决了！",
                ["我刚把说话动作的过渡时间改完"], default);

            Assert.Equal(RecallStatus.Found, result.Status);
            Assert.True(conversations.IsCurrentSource(scope, Assert.Single(result.Sources)));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Bare_continuation_without_recent_context_stays_empty()
    {
        var repository = new Repository();
        var result = await Create(repository).RetrieveAsync(new("mash", "a"), "current", "终于解决了！",
            ["今天天气不错啊"], default);
        Assert.Equal(RecallStatus.Empty, result.Status);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Continuation_spanning_two_conversations_abstains_instead_of_guessing()
    {
        var repository = new Repository { MultiEvent = true };
        var result = await Create(repository).RetrieveAsync(new("mash", "a"), "current",
            "那个再调一点吧", ["动作参数还要改", "设置页间距也要收"], default);
        Assert.Equal(RecallStatus.Empty, result.Status);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Explicit_history_reference_keeps_its_original_behaviour()
    {
        // The 4-argument path is unchanged; the context overload must not alter it.
        var repository = new Repository();
        var legacy = await Create(repository).RetrieveAsync(new("mash", "a"), "current",
            "继续上次的面谈", default);
        Assert.Equal(RecallStatus.Found, legacy.Status);

        var withContext = await Create(repository).RetrieveAsync(new("mash", "a"), "current",
            "继续上次的面谈", ["随便说点什么"], default);
        Assert.Equal(RecallStatus.Found, withContext.Status);
    }

    [Fact]
    public async Task No_retrieval_hit_leaves_the_turn_empty()
    {
        var repository = new Repository();
        var result = await Create(repository).RetrieveAsync(new("mash", "a"), "current",
            "我去年生日许了什么愿", ["昨天聊了点别的"], default);
        Assert.Equal(RecallStatus.Empty, result.Status);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void Continuation_gate_is_on_by_default_and_still_switchable()
    {
        // Decision after the real-data acceptance run: enabled by default, because the gate
        // produced zero candidates across 24 greeting/small-talk probes. It must stay switchable
        // so a bad outcome can be reverted without a code change.
        Assert.True(ConversationRecallContinuationGate.Enabled);
        Assert.Equal(3, ConversationRecallContinuationGate.MaxRecentTurns);

        var original = ConversationRecallContinuationGate.Enabled;
        try
        {
            ConversationRecallContinuationGate.Enabled = false;
            Assert.False(ConversationRecallContinuationGate.Enabled);
            ConversationRecallContinuationGate.Enabled = true;
            Assert.True(ConversationRecallContinuationGate.Enabled);
        }
        finally
        {
            ConversationRecallContinuationGate.Enabled = original;
        }
    }

    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults with
        {
            ModelConnection = new("test", "https://fixture.test", "same", contextWindowOverride: 32768)
        };
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }

    private sealed class Resolver(Provider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }

    private sealed class Provider(string json) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "same";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new(json, IsComplete: true, FinishReason: "stop");
        }
    }

    private sealed class Repository : IConversationRecallRepository
    {
        public bool MultiEvent { get; init; }

        private static HistoryHit Hit(string id, string title, string text) =>
            new(new(id, id + "-m", 1, DateTimeOffset.UnixEpoch), title, text, false);

        public IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10) =>
            [Hit("interview", "导师面谈", "先讲信号项目")];

        public IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId,
            string query, int limit = 20)
        {
            if (MultiEvent)
                return [Hit("live2d", "Live2D", "动作参数还要改"), Hit("ui", "UI", "设置页间距也要收")];
            return query.Contains("解决了", StringComparison.Ordinal)
                ? [Hit("live2d", "Live2D", "说话动作的过渡时间问题终于解决了！")]
                : query.Contains("面谈", StringComparison.Ordinal)
                    ? [Hit("interview", "导师面谈", "先讲信号项目")]
                    : [];
        }

        public HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000,
            HistoryReadCursor? cursor = null) =>
            new([Hit(anchor.ConversationId, "Live2D", "我刚把说话动作的过渡时间改完")], null);
    }
}

[CollectionDefinition(nameof(ContinuationSwitchTests), DisableParallelization = true)]
public sealed class ContinuationSwitchTests;
