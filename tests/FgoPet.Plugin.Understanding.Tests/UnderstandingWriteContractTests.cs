using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class UnderstandingWriteContractTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "understanding-write-" + Guid.NewGuid().ToString("N"));
    private readonly ConversationScope scope = new("mash", "synthetic");
    private readonly Clock clock = new();
    private readonly RuntimeDatabase database;
    private readonly UnderstandingService owner;
    public UnderstandingWriteContractTests()
    {
        database = new(Path.Combine(directory, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        owner = new(database, new Sources(), clock);
        owner.Configure(scope, 0, new(UnderstandingMode.Bounded, ReadSources: true, SaveCandidates: true, UseInDialogue: true));
    }
    private ReviewOriginal Original(string id, string conversation = "scene-a")
    {
        var source = new ConversationSourceSnapshot(new(conversation, "mash", "synthetic"), id, new('A', 64),
            clock.Now, ConversationSourceKind.Assistant, "Synthetic", 1);
        const string text = "我现在倾向把主动参与做得低干扰一些。";
        return new(source, text, new(source, 0, text.Length, text.Length));
    }
    private UnderstandingProposal Proposal(string id, ReviewOriginal original, UnderstandingOrigin origin = UnderstandingOrigin.Expressed) =>
        new(id, UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "倾向低干扰参与", "仅当前项目，暂定",
            [original.Source], Origin: origin,
            Status: origin == UnderstandingOrigin.Expressed ? UnderstandingStatus.Tentative : UnderstandingStatus.Hypothesis,
            Expressions: origin == UnderstandingOrigin.Expressed ? [new(original.Source, original.Text)] : []);
    private UnderstandingReceipt Apply(string operation, UnderstandingProposal p, ReviewOriginal original, bool allow = true) =>
        owner.Apply(owner.Begin(scope, operation, [original.Source], coverage: [original.Coverage]), [p],
            classification: new Routing(allow), originals: [original]);
    private UnderstandingEntry Entry(string id) => owner.List(scope, false).Concat(owner.List(scope, true)).Single(e => e.Id == id);

    [Fact] public void Hypothesis_stays_out_of_query_even_after_human_approval_and_reopen()
    {
        var original = Original("a"); var p = Proposal("inferred", original, UnderstandingOrigin.SecondOrderInferred);
        Apply("create", p, original);
        Assert.Equal(UnderstandingStatus.Hypothesis, Entry(p.Id).Status);
        owner.Review(scope, "approve", p.Id, 1, UnderstandingAction.Approve);
        Assert.Empty(owner.Query(scope, "").Items);
        Assert.Empty(new UnderstandingService(database, new Sources(), clock).Query(scope, "").Items);
        Assert.Empty(owner.Query(scope, "", true, p.Id).Items);
    }

    [Fact] public void Attach_keeps_identity_content_all_sources_history_and_idempotent_receipt()
    {
        var first = Original("a"); var p = Proposal("stance", first); Apply("create", p, first);
        clock.Now = clock.Now.AddMinutes(1); var next = Original("b", "scene-b");
        var attach = Proposal("attachment", next) with { Operation = UnderstandingWriteOperation.AttachEvidence, TargetId = p.Id, TargetRevision = 1 };
        var receipt = Apply("attach", attach, next);
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(receipt.Items).Outcome);
        var entry = Assert.Single(owner.Query(scope, "").Items);
        Assert.Equal(p.Id, entry.Id); Assert.Equal(2, entry.Revision); Assert.Equal(2, entry.Sources.Count);
        Assert.Equal(p.Content, entry.Content); Assert.Equal(2, owner.Changes(scope, p.Id).Count);
        Assert.Equal(clock.Now, entry.LastReviewedAt);
        Assert.Equal(receipt.Items, Apply("attach", attach, next).Items);
    }

    [Fact] public void Independent_future_expression_promotes_same_hypothesis_without_losing_inference_history()
    {
        var first = Original("a"); var p = Proposal("inferred", first, UnderstandingOrigin.SecondOrderInferred); Apply("create", p, first);
        clock.Now = clock.Now.AddMinutes(1); var next = Original("b", "scene-b");
        var promote = Proposal("promotion", next) with { Operation = UnderstandingWriteOperation.AttachEvidence, TargetId = p.Id, TargetRevision = 1 };
        Apply("promote", promote, next);
        var entry = Assert.Single(owner.Query(scope, "").Items);
        Assert.Equal(p.Id, entry.Id); Assert.Equal(UnderstandingStatus.Tentative, entry.Status);
        Assert.Equal(2, entry.Sources.Count);
        Assert.Contains(owner.Changes(scope, p.Id), e => e.Origin == UnderstandingOrigin.SecondOrderInferred && e.Status == UnderstandingStatus.Hypothesis);
    }

    [Fact] public void Pending_revision_preserves_effective_target_until_exact_user_review()
    {
        var first = Original("a"); var p = Proposal("stance", first); Apply("create", p, first);
        clock.Now = clock.Now.AddMinutes(1); var next = Original("b", "scene-b");
        var revision = Proposal("qualified", next) with { Operation = UnderstandingWriteOperation.Revise, Content = "低价值打断应减少，明确截止日期例外",
            TargetId = p.Id, TargetRevision = 1, Conflicts = [next.Source] };
        Apply("revise", revision, next, allow: false);
        Assert.Equal(p.Id, Assert.Single(owner.Query(scope, "").Items).Id);
        owner.Review(scope, "accept-revision", revision.Id, 1, UnderstandingAction.Approve);
        Assert.Equal(revision.Id, Assert.Single(owner.Query(scope, "").Items).Id);
        Assert.Equal(UnderstandingLife.Retracted, Entry(p.Id).Life);
        Assert.Equal(2, Entry(revision.Id).Sources.Count);
    }

    [Fact] public void Automatic_retract_preserves_all_provenance_and_never_deletes_history()
    {
        var first = Original("a"); var p = Proposal("stance", first); Apply("create", p, first);
        clock.Now = clock.Now.AddMinutes(1); var next = Original("b", "scene-b");
        var retract = Proposal("retraction", next) with { Operation = UnderstandingWriteOperation.Retract,
            Status = UnderstandingStatus.Retracted, TargetId = p.Id, TargetRevision = 1, Conflicts = [next.Source] };
        Assert.Equal(UnderstandingOutcome.Applied, Assert.Single(Apply("retract", retract, next).Items).Outcome);
        Assert.Empty(owner.Query(scope, "").Items);
        Assert.Equal(UnderstandingLife.Retracted, Entry(p.Id).Life);
        Assert.Equal(UnderstandingStatus.Retracted, Entry(p.Id).Status);
        Assert.Equal(2, Entry(p.Id).Sources.Count); Assert.Equal(2, owner.Changes(scope, p.Id).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Maintaining_pending_replacement_preserves_original_target_until_manual_or_automatic_activation(bool automatic)
    {
        var first = Original("a"); var p = Proposal("stance", first); Apply("create", p, first);
        clock.Now = clock.Now.AddMinutes(1); var second = Original("b", "scene-b");
        var replacement = Proposal("replacement", second) with { Content = "带有明确例外的立场", Operation = UnderstandingWriteOperation.Revise,
            TargetId = p.Id, TargetRevision = 1 };
        Apply("revise", replacement, second, allow: false);
        clock.Now = clock.Now.AddMinutes(1); var third = Original("c", "scene-c");
        var attach = Proposal("more-evidence", third) with { Content = replacement.Content, Operation = UnderstandingWriteOperation.AttachEvidence,
            TargetId = replacement.Id, TargetRevision = 1 };
        Apply("attach", attach, third, allow: automatic);
        if (!automatic) owner.Review(scope, "accept", replacement.Id, 2, UnderstandingAction.Approve);
        Assert.Equal(replacement.Id, Assert.Single(owner.Query(scope, "").Items).Id);
        Assert.Equal(UnderstandingLife.Retracted, Entry(p.Id).Life);
    }

    [Fact] public void Same_batch_duplicate_creates_fail_atomically()
    {
        var original = Original("a"); var p = Proposal("one", original);
        var receipt = owner.Apply(owner.Begin(scope, "duplicates", [original.Source], coverage: [original.Coverage]),
            [p, p with { Id = "two" }], classification: new Routing(true), originals: [original]);
        Assert.All(receipt.Items, i => Assert.Equal(UnderstandingOutcome.Failed, i.Outcome));
        Assert.Empty(owner.List(scope, false)); Assert.Empty(owner.List(scope, true));
    }

    [Fact] public void Semantically_unsupported_self_is_no_op_instead_of_persisted_personality_candidate()
    {
        var original = Original("a"); var p = Proposal("unsupported", original);
        var receipt = owner.Apply(owner.Begin(scope, "unsupported", [original.Source], coverage: [original.Coverage]),
            [p], classification: new Unsupported(), originals: [original]);
        Assert.Equal(UnderstandingOutcome.NoChange, Assert.Single(receipt.Items).Outcome);
        Assert.Empty(owner.List(scope, true)); Assert.Empty(owner.List(scope, false));
    }

    [Fact] public void Automatic_activation_cannot_replace_a_predecessor_changed_after_pending_revision()
    {
        var first = Original("a"); var p = Proposal("stance", first); Apply("create", p, first);
        clock.Now = clock.Now.AddMinutes(1); var second = Original("b", "scene-b");
        var replacement = Proposal("replacement", second) with { Content = "带例外的观点", Operation = UnderstandingWriteOperation.Revise,
            TargetId = p.Id, TargetRevision = 1 };
        Apply("revise", replacement, second, allow: false);
        owner.Review(scope, "correction", p.Id, 1, UnderstandingAction.Correct, "较新的人工纠正");
        clock.Now = clock.Now.AddMinutes(1); var third = Original("c", "scene-c");
        var attach = Proposal("attach", third) with { Content = replacement.Content, Operation = UnderstandingWriteOperation.AttachEvidence,
            TargetId = replacement.Id, TargetRevision = 1 };
        Assert.Equal(UnderstandingOutcome.Failed, Assert.Single(Apply("activate", attach, third).Items).Outcome);
        Assert.Equal("较新的人工纠正", Assert.Single(owner.Query(scope, "").Items).Correction);
    }

    [Fact] public void Retraction_in_proposals_mode_is_reviewable_and_preserves_effective_target_until_approval()
    {
        var original = Original("a"); var p = Proposal("stance", original); Apply("create", p, original);
        owner.Review(scope, "accept", p.Id, 1, UnderstandingAction.Approve);
        owner.Configure(scope, owner.State(scope).Revision, new(UnderstandingMode.Proposals, ReadSources: true, SaveCandidates: true, UseInDialogue: true));
        var before = Entry(p.Id);
        clock.Now = clock.Now.AddMinutes(1); var next = Original("b", "scene-b");
        var retract = Proposal("retract-request", next) with { Operation = UnderstandingWriteOperation.Retract, Status = UnderstandingStatus.Retracted,
            TargetId = p.Id, TargetRevision = before.Revision, Conflicts = [next.Source] };
        Assert.Equal(UnderstandingOutcome.ProposedPending, Assert.Single(Apply("propose-retract", retract, next).Items).Outcome);
        Assert.Equal(p.Id, Assert.Single(owner.Query(scope, "").Items).Id);
        var pending = Assert.Single(owner.List(scope, true));
        owner.Review(scope, "approve-retract", pending.Id, pending.Revision, UnderstandingAction.Approve);
        Assert.Empty(owner.Query(scope, "").Items);
        Assert.Equal(UnderstandingLife.Retracted, Entry(p.Id).Life);
        Assert.Equal(UnderstandingStatus.Retracted, Entry(pending.Id).Status);
    }

    [Fact] public void Maintenance_cannot_turn_a_pending_withdrawal_into_an_ordinary_effective_stance()
    {
        var original = Original("a"); var p = Proposal("stance", original); Apply("create", p, original);
        clock.Now = clock.Now.AddMinutes(1); var next = Original("b", "scene-b");
        var retract = Proposal("withdrawal", next) with { Operation = UnderstandingWriteOperation.Retract, Status = UnderstandingStatus.Retracted,
            TargetId = p.Id, TargetRevision = 1, Conflicts = [next.Source] };
        Apply("propose-withdrawal", retract, next, allow: false);
        clock.Now = clock.Now.AddMinutes(1); var third = Original("c", "scene-c");
        var attach = Proposal("attachment", third) with { Operation = UnderstandingWriteOperation.AttachEvidence,
            TargetId = "withdrawal", TargetRevision = 1 };
        Assert.Equal(UnderstandingOutcome.Failed, Assert.Single(Apply("wrong-maintenance", attach, third).Items).Outcome);
        var pending = Assert.Single(owner.List(scope, true));
        Assert.Equal(UnderstandingWriteOperation.Retract, pending.ProposedOperation);
        Assert.Equal(p.Id, Assert.Single(owner.Query(scope, "").Items).Id);
        owner.Review(scope, "withdraw", pending.Id, pending.Revision, UnderstandingAction.Approve);
        Assert.Empty(owner.Query(scope, "").Items);
    }

    [Fact] public void Projection_caps_current_self_and_relationship_separately()
    {
        for (var i = 0; i < 5; i++)
        {
            var original = Original("s" + i, "scene" + i);
            Apply("self" + i, Proposal("self" + i, original) with { Content = "不同的当前立场" + i }, original);
            Apply("rel" + i, Proposal("rel" + i, original) with { Domain = UnderstandingDomain.Relationship,
                Kind = UnderstandingKind.InteractionPattern, Subject = "mash/user", Content = "不同互动模式" + i }, original);
        }
        var projected = owner.Query(scope, "").Items;
        Assert.Equal(3, projected.Count(e => e.Domain == UnderstandingDomain.Self));
        Assert.Equal(2, projected.Count(e => e.Domain == UnderstandingDomain.Relationship));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Sources : IConversationSourceReader
    {
        public bool IsCurrent(ConversationSourceSnapshot source) => true;
        public ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind) => throw new NotSupportedException();
    }
    private sealed class Routing(bool allow) : IUnderstandingRoutingPolicy, IUnderstandingEvidenceValidation
    {
        public bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => allow;
        public UnderstandingEvidenceSupport? Assess(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => UnderstandingEvidenceSupport.Supported;
    }
    private sealed class Unsupported : IUnderstandingRoutingPolicy
    {
        public bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => false;
        public bool IsUnsupported(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> roots) => true;
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
}
