using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class EvidenceValidationGateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "evidence-gate-" + Guid.NewGuid().ToString("N"));
    private readonly ConversationScope scope = new("mash", "synthetic");
    private readonly RuntimeDatabase database;
    private readonly UnderstandingService owner;
    private readonly ReviewOriginal original;
    private readonly UnderstandingProposal proposal;

    public EvidenceValidationGateTests()
    {
        database = new(Path.Combine(directory, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        owner = new(database, new Sources());
        const string text = "在低代价且可撤回的比较中，我先小试，以实际反馈区分方案。";
        var source = new ConversationSourceSnapshot(new("scene", "mash", "synthetic"), "reply", new('A', 64),
            DateTimeOffset.UtcNow.AddMinutes(-1), ConversationSourceKind.Assistant, "Synthetic", 1);
        original = new(source, text, new(source, 0, text.Length, text.Length));
        proposal = new("candidate", UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", text,
            "仅限非紧急、低代价且可撤回的比较。", [source], Origin: UnderstandingOrigin.Expressed,
            Status: UnderstandingStatus.Tentative, Expressions: [new(source, text)]);
    }

    private void Configure(UnderstandingMode mode) => owner.Configure(scope, owner.State(scope).Revision,
        new(mode, ReadSources: true, SaveCandidates: mode != UnderstandingMode.Off, UseInDialogue: true));

    private UnderstandingReceipt Apply(string id, IUnderstandingRoutingPolicy? policy = null, bool manual = false) =>
        owner.Apply(owner.Begin(scope, id, [original.Source], manual: manual, coverage: [original.Coverage]),
            [proposal], classification: policy, originals: [original]);

    [Theory]
    [InlineData(UnderstandingMode.Proposals)]
    [InlineData(UnderstandingMode.Bounded)]
    public void Missing_validation_cannot_fall_back_to_persisting_a_pending_candidate(UnderstandingMode mode)
    {
        Configure(mode);
        var receipt = Apply("missing", new Routing());
        Assert.Equal(UnderstandingOutcome.NoChange, Assert.Single(receipt.Items).Outcome);
        Assert.Equal("UNDERSTANDING_EVIDENCE_VALIDATION_REQUIRED", Assert.Single(receipt.Items).Code);
        Assert.Empty(owner.List(scope, true));
        Assert.Empty(owner.List(scope, false));
        Assert.Empty(new UnderstandingService(database, new Sources()).List(scope, true));
    }

    [Fact]
    public void Unvalidated_manual_candidate_remains_a_preview_but_approval_cannot_persist_it()
    {
        Configure(UnderstandingMode.Off);
        Assert.Equal(UnderstandingOutcome.ReviewedNotPersisted, Assert.Single(Apply("preview", manual: true).Items).Outcome);
        var preview = Assert.Single(owner.List(scope, true));
        Assert.Equal(proposal.Content, preview.Content);
        var receipt = owner.Review(scope, "approve", preview.Id, preview.Revision, UnderstandingAction.Approve);
        Assert.Equal(UnderstandingOutcome.Failed, Assert.Single(receipt.Items).Outcome);
        Assert.Equal("UNDERSTANDING_EVIDENCE_VALIDATION_REQUIRED", Assert.Single(receipt.Items).Code);
        Assert.Empty(new UnderstandingService(database, new Sources()).List(scope, false));
    }

    [Theory]
    [InlineData(UnderstandingMode.Proposals, UnderstandingEvidenceSupport.Supported, true)]
    [InlineData(UnderstandingMode.Bounded, UnderstandingEvidenceSupport.Supported, true)]
    [InlineData(UnderstandingMode.Proposals, UnderstandingEvidenceSupport.Partial, false)]
    [InlineData(UnderstandingMode.Bounded, UnderstandingEvidenceSupport.Partial, false)]
    [InlineData(UnderstandingMode.Proposals, UnderstandingEvidenceSupport.Unsupported, false)]
    [InlineData(UnderstandingMode.Bounded, UnderstandingEvidenceSupport.Contradicted, false)]
    public async Task Parsed_validation_controls_actual_persistence_independently_of_low_risk_routing(
        UnderstandingMode mode, UnderstandingEvidenceSupport support, bool saved)
    {
        Configure(mode);
        var input = new ReviewModelInput("grade", scope, [original], [], _ => { });
        var provider = new EvidenceValidationProtocolTests.Provider(support.ToString().ToUpperInvariant());
        var route = ModelRouteKey.From(new FgoPet.Core.Settings.ModelConnectionSettings("deepseek", "https://example.invalid/v1", "test"));
        var policy = await UnderstandingSemanticClassifier.ClassifyAsync(input, [proposal], provider, route,
            new RequestTokenMeter(), default);
        var receipt = Apply("save", policy);
        Assert.Equal(saved ? 1 : 0, owner.List(scope, true).Count + owner.List(scope, false).Count);
        if (saved)
        {
            var entry = Assert.Single(owner.List(scope, true).Concat(owner.List(scope, false)));
            Assert.Equal(proposal.Content, entry.Content);
            Assert.Equal(proposal.Sources, entry.Sources);
            Assert.Equal(proposal.Expressions, entry.Expressions);
            Assert.Equal(mode == UnderstandingMode.Proposals ? UnderstandingEffect.Pending : UnderstandingEffect.Bounded, entry.Effect);
        }
        else
        {
            Assert.Equal(UnderstandingOutcome.NoChange, Assert.Single(receipt.Items).Outcome);
            Assert.Equal("UNDERSTANDING_EVIDENCE_" + support.ToString().ToUpperInvariant(), Assert.Single(receipt.Items).Code);
        }
        Assert.Equal(receipt.Items, Apply("save", policy).Items);
    }

    [Theory]
    [InlineData(UnderstandingWriteOperation.AttachEvidence, UnderstandingEvidenceSupport.Partial)]
    [InlineData(UnderstandingWriteOperation.Revise, UnderstandingEvidenceSupport.Unsupported)]
    [InlineData(UnderstandingWriteOperation.Retract, UnderstandingEvidenceSupport.Contradicted)]
    public void Rejected_mutation_preserves_old_meaning_sources_history_and_revision(
        UnderstandingWriteOperation operation, UnderstandingEvidenceSupport support)
    {
        Configure(UnderstandingMode.Bounded);
        Apply("seed", new Validation(UnderstandingEvidenceSupport.Supported));
        var before = Assert.Single(owner.List(scope, false));
        var nextSource = original.Source with { MessageId = "later", Scope = new("later-scene", "mash", "synthetic") };
        var next = new ReviewOriginal(nextSource, original.Text, new(nextSource, 0, original.Text.Length, original.Text.Length));
        var mutation = proposal with { Id = "mutation", Operation = operation, TargetId = before.Id, TargetRevision = before.Revision,
            Sources = [nextSource], Expressions = [new(nextSource, next.Text)],
            Status = operation == UnderstandingWriteOperation.Retract ? UnderstandingStatus.Retracted : proposal.Status,
            Conflicts = operation == UnderstandingWriteOperation.Retract ? [nextSource] : [] };
        var receipt = owner.Apply(owner.Begin(scope, "mutate", [nextSource], coverage: [next.Coverage]), [mutation],
            classification: new Validation(support), originals: [next]);
        Assert.Equal(UnderstandingOutcome.NoChange, Assert.Single(receipt.Items).Outcome);
        var after = Assert.Single(owner.List(scope, false));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
        Assert.Single(owner.Changes(scope, before.Id));
        Assert.Empty(owner.List(scope, true));
    }

    [Fact]
    public void Supported_preview_can_be_approved_but_editing_it_invalidates_validation()
    {
        Configure(UnderstandingMode.Off);
        Apply("preview", new Validation(UnderstandingEvidenceSupport.Supported), manual: true);
        var preview = Assert.Single(owner.List(scope, true));
        owner.Review(scope, "correct", preview.Id, preview.Revision, UnderstandingAction.Correct, "另一条未经 validation 的命题");
        var changed = Assert.Single(owner.List(scope, true));
        var blocked = owner.Review(scope, "approve-edited", changed.Id, changed.Revision, UnderstandingAction.Approve);
        Assert.Equal("UNDERSTANDING_EVIDENCE_VALIDATION_REQUIRED", Assert.Single(blocked.Items).Code);
        Assert.Empty(new UnderstandingService(database, new Sources()).List(scope, false));
    }

    [Theory]
    [InlineData(UnderstandingEvidenceSupport.Supported, true)]
    [InlineData(UnderstandingEvidenceSupport.Partial, false)]
    [InlineData(UnderstandingEvidenceSupport.Unsupported, false)]
    [InlineData(UnderstandingEvidenceSupport.Contradicted, false)]
    public void Preview_approval_requires_supported_validation(UnderstandingEvidenceSupport support, bool allowed)
    {
        Configure(UnderstandingMode.Off);
        Apply("preview", new Validation(support), manual: true);
        var preview = Assert.Single(owner.List(scope, true));
        var receipt = owner.Review(scope, "approve", preview.Id, preview.Revision, UnderstandingAction.Approve);
        Assert.Equal(allowed ? UnderstandingOutcome.Applied : UnderstandingOutcome.Failed, Assert.Single(receipt.Items).Outcome);
        Assert.Equal(allowed ? 1 : 0, new UnderstandingService(database, new Sources()).List(scope, false).Count);
    }

    [Fact]
    public void Supported_does_not_override_pending_routing_or_promote_inferred_hypotheses()
    {
        Configure(UnderstandingMode.Bounded);
        var inferred = proposal with { Origin = UnderstandingOrigin.SecondOrderInferred, Status = UnderstandingStatus.Hypothesis, Expressions = [] };
        owner.Apply(owner.Begin(scope, "inferred", [original.Source], coverage: [original.Coverage]), [inferred],
            classification: new Validation(UnderstandingEvidenceSupport.Supported), originals: [original]);
        var pending = Assert.Single(owner.List(scope, true));
        Assert.Equal(UnderstandingStatus.Hypothesis, pending.Status);
        Assert.Empty(owner.Query(scope, "").Items);
    }

    internal sealed class Validation(UnderstandingEvidenceSupport support) : IUnderstandingRoutingPolicy, IUnderstandingEvidenceValidation
    {
        public bool IsLowImpact(UnderstandingProposal p, IReadOnlyList<ConversationSourceSnapshot> roots) => true;
        public UnderstandingEvidenceSupport? Assess(UnderstandingProposal p, IReadOnlyList<ConversationSourceSnapshot> roots) => support;
    }

    private sealed class Routing : IUnderstandingRoutingPolicy
    { public bool IsLowImpact(UnderstandingProposal p, IReadOnlyList<ConversationSourceSnapshot> roots) => true; }
    private sealed class Sources : IConversationSourceReader
    {
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
        public ConversationSourceSnapshot Read(ToolScope s, string id, ConversationSourceKind kind) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
