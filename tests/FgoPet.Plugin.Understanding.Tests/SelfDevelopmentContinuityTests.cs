using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

/// <summary>
/// Owner-contract fixture: real UnderstandingService, plugin and temporary SQLite;
/// synthetic assistant source snapshots, test routing and no LLM/Dialogue I/O.
/// This checks persistence and projection, NOT emergent Self Development.
/// </summary>
public sealed class SelfDevelopmentContinuityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "fgopet-self-development-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly ConversationScope _scope = new("mash", "self-development-fixture");
    private readonly MutableClock _clock = new();
    private readonly SyntheticSources _sources = new();
    private readonly RuntimeDatabase _database;
    private readonly UnderstandingService _owner;

    public SelfDevelopmentContinuityTests()
    {
        _database = new RuntimeDatabase(Path.Combine(_directory, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _owner = new UnderstandingService(_database, _sources, _clock, new SyntheticStanceRouting());
        _owner.Configure(_scope, 0,
            new UnderstandingPolicy(UnderstandingMode.Bounded,
                ReadSources: true, SaveCandidates: true, UseInDialogue: true));
    }

    private ConversationSourceSnapshot Assistant(string conversationId, string messageId, long sourceRevision) =>
        new(new(conversationId, _scope.ServantId, _scope.ProjectId),
            messageId, new string('A', 64), _clock.GetUtcNow(),
            ConversationSourceKind.Assistant, "Synthetic completed assistant reply", sourceRevision);

    private UnderstandingProposal Stance(string id, ConversationSourceSnapshot source, string content,
        string? replacesId = null, int? replacesRevision = null) =>
        new(id, UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", content,
            "Synthetic, tentative Live2D stance; assistant expression, not observed video or permanent identity",
            [source], replacesId, replacesRevision);

    private UnderstandingReceipt Apply(UnderstandingService owner, string operation, UnderstandingProposal proposal)
    {
        var ticket = owner.Begin(_scope, operation, proposal.Sources,
            coverage: proposal.Sources.Select(s => new UnderstandingSourceCoverage(s, 0, 8, 8)).ToArray());
        return owner.Apply(ticket, [proposal]);
    }

    [Fact]
    public async Task Dated_self_stance_round_trips_and_revisions_replace_prior_projection_in_new_conversations()
    {
        // S1: synthetic *assistant* utterance => current tentative stance.
        var first = Stance("stance-day1", Assistant("conversation-day1", "mash-answer-1", 1),
            "我目前倾向抬头动作轻量、短促，避免在每次回复时重复播放。");
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply(_owner, "create-day1", first).Items).Outcome);
        var firstEntry = Assert.Single(_owner.Query(_scope, "").Items);
        Assert.Equal(UnderstandingKind.CurrentStance, firstEntry.Kind);
        Assert.Equal("assistant_stance", firstEntry.BasisKind);
        Assert.Equal(UnderstandingReview.Unreviewed, firstEntry.Review);
        Assert.Equal(UnderstandingEffect.Bounded, firstEntry.Effect);
        Assert.Equal(7d, (firstEntry.ActiveUntil - firstEntry.ObservedAt).TotalDays);

        // S2: new conversation, reopened owner, no copied prior transcript.
        _clock.Now = _clock.Now.AddDays(1);
        var reopened = new UnderstandingService(_database, _sources, _clock);
        await using (var session2 = new UnderstandingPlugin(reopened, _ => true, new SyntheticResolver()))
        {
            await session2.StartAsync(default);
            var ordinary2 = Assert.Single(session2.BuildPrompt(
                new ToolScope("conversation-day2", "mash", _scope.ProjectId),
                "后来我把频率降低了", true));
            Assert.Contains(first.Content, ordinary2.Text);
            Assert.Contains(first.Conditions, ordinary2.Text);
        }

        var second = Stance("stance-day2", Assistant("conversation-day2", "mash-answer-2", 2),
            "根据前辈对低频动作的反馈，我暂时倾向保留简短抬头，但避免固定欢迎动作。",
            first.Id, firstEntry.Revision);
        Assert.Equal(UnderstandingOutcome.Applied,
            Assert.Single(Apply(_owner, "revise-day2", second).Items).Outcome);
        Assert.Equal(UnderstandingLife.Retracted,
            Assert.Single(_owner.List(_scope, pending: false).Where(e => e.Id == first.Id)).Life);
        var secondEntry = Assert.Single(_owner.Query(_scope, "").Items);
        Assert.Equal(second.Id, secondEntry.Id);
        Assert.Equal(first.Id, secondEntry.ReplacesId);

        // S3: new supported assistant utterance narrows earlier claim.
        _clock.Now = _clock.Now.AddDays(1);
        var third = Stance("stance-day3", Assistant("conversation-day3", "mash-answer-3", 3),
            "我修正了一般判断：等待阶段可以短暂抬头，真正开口时应减少这个动作。",
            second.Id, secondEntry.Revision);
        Assert.Equal(UnderstandingOutcome.Applied,
            Assert.Single(Apply(_owner, "revise-day3", third).Items).Outcome);
        var latest = Assert.Single(_owner.Query(_scope, "").Items);
        Assert.Equal(third.Id, latest.Id);
        Assert.Equal(second.Id, latest.ReplacesId);
        Assert.Equal(2, _owner.List(_scope, pending: false).Count(e => e.Life == UnderstandingLife.Retracted));

        // S4: effective state is projected via existing ordinary, Native and query paths.
        // This does not evaluate the model's actual answer to the QQ-notification question.
        _clock.Now = _clock.Now.AddDays(1);
        var day4 = new ToolScope("conversation-day4", "mash", _scope.ProjectId);
        var fresh = new UnderstandingService(_database, _sources, _clock);
        await using var session4 = new UnderstandingPlugin(fresh, _ => true, new SyntheticResolver());
        await session4.StartAsync(default);
        var ordinary4 = Assert.Single(session4.BuildPrompt(day4, "如果以后接入 QQ 作业通知呢？", true));
        var native4 = Assert.Single(await session4.BuildAsync(
            new AgentContextRequest(day4, "run-day4", 1,
                new ProtectedQueryReference("user-day4", new string('A', 64)), 8192, []), default));
        Assert.Equal(ordinary4.Text, native4.Content);
        Assert.Contains(third.Content, ordinary4.Text);
        Assert.Contains(third.Conditions, ordinary4.Text);
        Assert.DoesNotContain(first.Content, ordinary4.Text);
        Assert.DoesNotContain(second.Content, ordinary4.Text);
        var query = await Assert.Single(session4.Contributions.Tools).InvokeAsync(
            new ToolInvocation(day4, JsonSerializer.SerializeToElement(new { query = "" })), default);
        Assert.True(query.Success);
        Assert.Contains(third.Content, query.Payload.GetRawText());
        Assert.DoesNotContain(first.Content, query.Payload.GetRawText());
        Assert.Empty(fresh.Query(new ConversationScope("mash", "other-project"), "").Items);
    }

    [Fact]
    public void Unclassified_self_stance_remains_pending_until_exact_version_approval()
    {
        var noRouting = new UnderstandingService(_database, _sources, _clock);
        var proposal = Stance("needs-review", Assistant("conversation-review", "assistant-review", 4),
            "我现在更想观察等待阶段与开口阶段的区别。");
        Assert.Equal(UnderstandingOutcome.ProposedPending,
            Assert.Single(Apply(noRouting, "pending-create", proposal).Items).Outcome);
        Assert.Empty(noRouting.Query(_scope, "").Items);
        var pending = Assert.Single(noRouting.List(_scope, pending: true));
        Assert.Equal(UnderstandingReview.Unreviewed, pending.Review);
        var approved = noRouting.Review(_scope, "explicit-approve",
            pending.Id, pending.Revision, UnderstandingAction.Approve);
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(approved.Items).Outcome);
        var active = Assert.Single(noRouting.Query(_scope, "").Items);
        Assert.Equal(UnderstandingReview.Approved, active.Review);
        Assert.Equal(UnderstandingEffect.Approved, active.Effect);
        Assert.Equal("assistant_stance", active.BasisKind);
    }

    [Fact]
    public void Assistant_only_open_question_and_user_fact_cannot_be_promoted_as_self()
    {
        var root = Assistant("conversation-invalid", "assistant-invalid", 6);
        var question = new UnderstandingProposal("self-question",
            UnderstandingDomain.Self, UnderstandingKind.OpenQuestion, "assistant",
            "为什么等待时抬头可能更自然？", "Synthetic question", [root]);
        var deniedQuestion = Assert.Single(Apply(_owner, "unsupported-question", question).Items);
        Assert.Equal(UnderstandingOutcome.Failed, deniedQuestion.Outcome);
        Assert.Equal("UNDERSTANDING_INSUFFICIENT_ROOTS", deniedQuestion.Code);

        var userFact = new UnderstandingProposal("pseudo-user-fact",
            UnderstandingDomain.Self, UnderstandingKind.UserFact, "assistant",
            "前辈永远不喜欢任何主动提醒。", "Unsupported as Self", [root]);
        var deniedFact = Assert.Single(Apply(_owner, "unsupported-fact", userFact).Items);
        Assert.Equal(UnderstandingOutcome.Failed, deniedFact.Outcome);
        Assert.Equal("UNDERSTANDING_MEMORY_OWNER_REQUIRED", deniedFact.Code);
        Assert.Empty(_owner.Query(_scope, "").Items);
    }

    [Fact]
    public void Expired_stance_and_revoked_access_are_not_projected()
    {
        var old = Stance("expiring-stance", Assistant("conversation-expiry", "assistant-expiry", 7),
            "我暂时想观察何时使用更轻量的动作。");
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply(_owner, "create-expiring", old).Items).Outcome);
        _clock.Now = _clock.Now.AddDays(8);
        Assert.Empty(_owner.Query(_scope, "").Items);
        Assert.Equal(UnderstandingLife.Dormant,
            Assert.Single(_owner.List(_scope, pending: false)).Life);
        var state = _owner.State(_scope);
        _owner.Configure(_scope, state.Revision, new UnderstandingPolicy(UnderstandingMode.Off));
        var denied = _owner.Query(_scope, "");
        Assert.Empty(denied.Items);
        Assert.Equal("UNDERSTANDING_NOT_AUTHORIZED", denied.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class SyntheticSources : IConversationSourceReader
    {
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) =>
            throw new NotSupportedException("Synthetic fixture has no real Dialogue owner.");
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
    }
    private sealed class SyntheticStanceRouting : IUnderstandingRoutingPolicy
    {
        // This is a test substitute, not the production independent semantic classifier.
        public bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) =>
            proposal.Domain == UnderstandingDomain.Self &&
            proposal.Kind == UnderstandingKind.CurrentStance &&
            proposal.Subject == "assistant" &&
            roots.Count > 0 && roots.All(s => s.Kind == ConversationSourceKind.Assistant);
    }
    private sealed class SyntheticResolver : IAgentQueryResolver
    {
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference,
            CancellationToken token) => ValueTask.FromResult<string?>("");
    }
}
