using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class RecurrenceRuntimeTests
{
    [Theory] [InlineData(0,60)] [InlineData(120,120)]
    public async Task Host_can_budget_the_multi_stage_task_without_changing_the_default_deadline(int requested,int expected)
    {
        var scope=new ConversationScope("mash","p");var sources=new Sources();sources.Add("c1");var clock=new DeadlineClock();
        var db=new RuntimeDatabase(Path.Combine(Path.GetTempPath(),"fgo-rec-deadline-"+Guid.NewGuid().ToString("N"),"runtime.db"),pooling:false);
        new RuntimeDatabaseMigrator(db).Migrate();var owner=new UnderstandingService(db,sources);
        owner.Configure(scope,0,new(UnderstandingMode.Bounded,ReadSources:true,SaveCandidates:true,ExternalProcessing:true));
        var service=new UnderstandingReviewService(owner,sources,sources,new Models(),new DialogueContextLifetime(),clock,
            taskTimeout:requested==0?null:TimeSpan.FromSeconds(requested));
        await service.ManualAsync(scope,"deadline",sources.Heads);
        Assert.Equal(TimeSpan.FromSeconds(expected),clock.DueTime);
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(121)]
    public void Host_task_deadline_cannot_be_infinite_or_exceed_two_minutes(int seconds)
        =>Assert.Throws<ArgumentOutOfRangeException>(()=>new UnderstandingReviewService(null!,null!,null!,null!,null!,TimeProvider.System,
            taskTimeout:TimeSpan.FromSeconds(seconds)));
    private sealed class DeadlineClock:TimeProvider
    {
        public TimeSpan DueTime;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {DueTime=dueTime;return base.CreateTimer(callback,state,dueTime,period);}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task A_validated_clue_survives_later_review_or_classifier_failure_and_restart(bool classifier)
    {
        var scope = new ConversationScope("mash", "p"); var sources = new Sources(); var models = new Models { FailReview = !classifier, FailClassifier = classifier };
        var db = new RuntimeDatabase(Path.Combine(Path.GetTempPath(), "fgo-rec-failure-" + Guid.NewGuid().ToString("N"), "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(db).Migrate(); var owner = new UnderstandingService(db, sources);
        owner.Configure(scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, SaveCandidates: true, ExternalProcessing: true));
        var service = new UnderstandingReviewService(owner, sources, sources, models, new DialogueContextLifetime(), TimeProvider.System);
        sources.Add("c1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ManualAsync(scope, "failed-self-review", sources.Heads.ToArray()));
        var restarted = new UnderstandingService(db, sources);
        var clue = Assert.Single(restarted.RecurrenceClues(scope));
        Assert.False(clue.Projectable); Assert.Equal(2, clue.SourceRefs.Count);
        Assert.Empty(restarted.List(scope, true)); Assert.Empty(restarted.List(scope, false));
        Assert.Empty(restarted.Query(scope, "").Items); Assert.Null(restarted.Receipt(scope, "failed-self-review"));
    }
    [Theory] [InlineData(true, false, 4)] [InlineData(false, false, 2)] [InlineData(true, true, 2)]
    public async Task Automatic_partner_discovery_rereads_raw_sources_only_with_host_attested_independence(bool isolated, bool sameConversation, int expectedOriginals)
    {
        var scope = new ConversationScope("mash", "p"); var sources = new Sources(); var models = new Models();
        var db = new RuntimeDatabase(Path.Combine(Path.GetTempPath(), "fgo-rec-runtime-" + Guid.NewGuid().ToString("N"), "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(db).Migrate(); var owner = new UnderstandingService(db, sources);
        owner.Configure(scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, SaveCandidates: true, ExternalProcessing: true));
        var service = new UnderstandingReviewService(owner, sources, sources, models, new DialogueContextLifetime(), TimeProvider.System);
        sources.Add("c1"); await service.ManualAsync(scope, "event-one", sources.Heads.ToArray());
        var clue = Assert.Single(owner.RecurrenceClues(scope)); var reads = sources.Reads;
        sources.Isolation = isolated; sources.Add(sameConversation ? "c1" : "c2");
        await service.ManualAsync(scope, "event-two", sources.Heads.TakeLast(2).ToArray());
        Assert.Equal(expectedOriginals, models.Reviewed!.Originals.Count);
        Assert.Equal(expectedOriginals, sources.Reads - reads);
        Assert.All(models.Reviewed.Originals, o => Assert.Equal(Sources.Raw, o.Text));
        Assert.DoesNotContain(models.Reviewed.Originals, o => o.Text == clue.Summary);
        Assert.Empty(owner.Query(scope, "").Items);
        Assert.Equal(isolated && !sameConversation ? 1 : 0, models.CluesAvailable!.Count);
    }
    private sealed class Models : IReviewModelFactory, IReviewModel, IRecurrenceModel
    {
        public bool FailReview, FailClassifier;
        public Task<IUnderstandingRoutingPolicy?> ClassifyAsync(ReviewModelInput input, ReviewModelResult result, UnderstandingState state, IChatDispatchGate gate, CancellationToken token)
            => FailClassifier ? throw new InvalidOperationException("SYNTHETIC_CLASSIFIER_FAILURE") : Task.FromResult<IUnderstandingRoutingPolicy?>(null);
        public ReviewModelInput? Reviewed; public IReadOnlyList<RecurrenceClue>? CluesAvailable;
        public bool Available(ConversationScope scope) => true;
        public IReviewModel Create(ConversationScope scope, UnderstandingState state, IChatDispatchGate gate) => this;
        public IRecurrenceModel CreateRecurrence(ConversationScope scope, UnderstandingState state, IChatDispatchGate gate) => this;
        public Task<RecurrenceDiscovery> DiscoverAsync(ReviewModelInput current, IReadOnlyList<RecurrenceClue> available, CancellationToken token = default)
        {
            CluesAvailable = available;
            return Task.FromResult(new RecurrenceDiscovery([new("reasoned comparison", "Assistant compared reasons before deciding.",
                [new(current.Originals.Last().Source, Sources.Raw)])], available.Take(3).Select(c => c.Id).ToArray()));
        }
        public Task<ReviewModelResult> ReviewAsync(ReviewModelInput input, CancellationToken token = default)
        {
            if (FailReview) throw new InvalidOperationException("SYNTHETIC_REVIEW_FAILURE");
            Reviewed = input;
            return Task.FromResult(new ReviewModelResult([], input.Originals.Select(o => o.Coverage).ToArray(), new(10, 10),
                new(10, TokenCountKind.Estimated, new('A', 64)), new('B', 64)));
        }
    }
    private sealed class Sources : IExperienceTimeline, IExperienceSourceReader, IRecurrenceIsolationReader
    {
        public const string Raw = "Synthetic original";
        public List<ExperienceSourceHead> Heads = []; public int Reads; public bool Isolation = true;
        public void Add(string conversation)
        {
            foreach (var kind in new[] { ConversationSourceKind.User, ConversationSourceKind.Assistant })
            {
                var n = Heads.Count + 1; var at = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(n);
                var source = new ConversationSourceSnapshot(new(conversation, "mash", "p"), "m" + n,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Raw))), at, kind, "Synthetic", n);
                Heads.Add(new(new(conversation, source.MessageId, n, at), source, Raw.Length));
            }
        }
        public bool IsIsolated(ConversationSourceSnapshot source) => Isolation && IsCurrent(source);
        public bool IsCurrent(ConversationSourceSnapshot source) => Heads.Any(h => h.Source == source);
        public ConversationSourceSnapshot Read(ToolScope scope, string id, ConversationSourceKind kind) => Heads.Single(h => h.Source.Scope == scope && h.Source.MessageId == id).Source;
        public long Boundary(ConversationScope scope) => Heads.Count;
        public long InputRevision(ConversationScope scope) => Heads.Count;
        public ExperienceSourceRead Read(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default)
        { Reads++; var head = Heads.Single(h => h.Anchor == anchor); return new(ExperienceSourceStatus.Available, new(scope, anchor, head.Source.Kind, Raw, head.Source.Fingerprint)); }
        public ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1, ExperienceWindowCursor? cursor = null, int maxItems = 8, int maxScalars = 6000, CancellationToken token = default) => throw new NotSupportedException();
        public ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default) => throw new NotSupportedException();
    }
}
