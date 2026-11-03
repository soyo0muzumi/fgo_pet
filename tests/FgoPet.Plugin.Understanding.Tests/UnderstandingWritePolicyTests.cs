using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class UnderstandingWritePolicyTests
{
    private static readonly ConversationScope Scope = new("mash", "project");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10);
    private const string Text = "I currently prefer keeping interruptions useful and brief.";
    private static ConversationSourceSnapshot Root(string id = "new", string conversation = "future", int days = 1,
        ConversationSourceKind kind = ConversationSourceKind.Assistant) =>
        new(new(conversation, "mash", "project"), id, new('A', 64), Now.AddDays(days), kind, null);
    private static ReviewOriginal Original(ConversationSourceSnapshot root, bool complete = true) =>
        new(root, Text, new(root, 0, Text.Length, complete ? Text.Length : Text.Length + 1));
    private static UnderstandingProposal Proposal(ConversationSourceSnapshot? root = null) => new("candidate",
        UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "Keep interruptions useful and brief.",
        "Current project only.", [root ?? Root()], Origin: UnderstandingOrigin.Expressed,
        Status: UnderstandingStatus.Tentative, Expressions: [new(root ?? Root(), "prefer keeping interruptions useful and brief")]);
    private static UnderstandingEntry Prior(UnderstandingOrigin origin = UnderstandingOrigin.SecondOrderInferred,
        UnderstandingStatus status = UnderstandingStatus.Hypothesis) => new("prior", Scope, 1,
        UnderstandingDomain.Self, UnderstandingKind.CurrentStance, "assistant", "Keep interruptions useful and brief.",
        "Current project only.", "model_inferred", [Root("old", "past", -1)], UnderstandingReview.Unreviewed,
        UnderstandingEffect.Pending, UnderstandingLife.Active, Now, Now.AddDays(7), 1, Origin: origin, Status: status);
    private static string? Validate(UnderstandingProposal proposal, UnderstandingEntry? target = null,
        IReadOnlyList<ReviewOriginal>? originals = null, IReadOnlyList<UnderstandingSourceCoverage>? coverage = null,
        IReadOnlyList<UnderstandingEntry>? existing = null)
    {
        originals ??= proposal.Sources.Select(s => Original(s)).ToArray();
        var ticket = new UnderstandingTicket("ticket", Scope, 1, 1, false, proposal.Sources,
            coverage ?? originals.Select(o => o.Coverage).ToArray());
        return UnderstandingWritePolicy.Validate(ticket, proposal, existing ?? (target is null ? [] : [target]), originals, Now.AddDays(3));
    }
    private static UnderstandingProposal Targeted(UnderstandingWriteOperation operation = UnderstandingWriteOperation.AttachEvidence) =>
        Proposal() with { TargetId = "prior", TargetRevision = 1, Operation = operation };

    [Fact] public void Expressed_tentative_has_exact_complete_assistant_evidence() => Assert.Null(Validate(Proposal()));

    [Theory]
    [InlineData(UnderstandingOrigin.Enacted)]
    [InlineData(UnderstandingOrigin.SecondOrderInferred)]
    [InlineData(UnderstandingOrigin.ExternallySuggested)]
    public void Non_expressed_creation_stays_hypothesis(UnderstandingOrigin origin)
    {
        Assert.Null(Validate(Proposal() with { Origin = origin, Status = UnderstandingStatus.Hypothesis, Expressions = null }));
        Assert.NotNull(Validate(Proposal() with { Origin = origin, Expressions = null }));
    }

    [Fact] public void Automatic_established_is_denied() =>
        Assert.NotNull(Validate(Proposal() with { Status = UnderstandingStatus.Established }));

    [Theory]
    [InlineData(UnderstandingDomain.User, UnderstandingKind.CurrentStance)]
    [InlineData(UnderstandingDomain.World, UnderstandingKind.CurrentStance)]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.StableIdentity)]
    [InlineData(UnderstandingDomain.Self, UnderstandingKind.SelfReflection)]
    [InlineData(UnderstandingDomain.Relationship, UnderstandingKind.CurrentStance)]
    public void Only_contract_owner_kind_pairs_are_writable(UnderstandingDomain domain, UnderstandingKind kind) =>
        Assert.NotNull(Validate(Proposal() with { Domain = domain, Kind = kind }));

    [Theory]
    [InlineData(UnderstandingKind.CurrentStance)]
    [InlineData(UnderstandingKind.CurrentInterest)]
    [InlineData(UnderstandingKind.Uncertainty)]
    [InlineData(UnderstandingKind.InteractionTendency)]
    [InlineData(UnderstandingKind.SelfObservation)]
    public void Five_self_kinds_are_allowed(UnderstandingKind kind) => Assert.Null(Validate(Proposal() with { Kind = kind }));

    [Fact] public void Relationship_interaction_pattern_is_allowed() => Assert.Null(Validate(Proposal() with {
        Domain = UnderstandingDomain.Relationship, Kind = UnderstandingKind.InteractionPattern, Subject = "mash-user" }));

    [Fact] public void Exact_quote_cannot_be_forged_or_credited_to_user()
    {
        Assert.NotNull(Validate(Proposal() with { Expressions = [new(Root(), "fabricated statement")] }));
        Assert.NotNull(Validate(Proposal(Root(kind: ConversationSourceKind.User))));
        Assert.NotNull(Validate(Proposal() with { Expressions = null }));
    }

    [Fact] public void Partial_or_different_version_quote_cannot_establish_expression()
    {
        Assert.NotNull(Validate(Proposal(), originals: [Original(Root(), complete: false)]));
        Assert.NotNull(Validate(Proposal(), originals: [Original(Root() with { Fingerprint = new('B', 64) })]));
        Assert.NotNull(Validate(Proposal(), coverage: [new(Root(), 0, 2, Text.Length)]));
    }

    [Fact] public void One_future_independent_expression_promotes_hypothesis() => Assert.Null(Validate(Targeted(), Prior()));

    [Fact] public void Old_same_context_or_same_reference_cannot_promote()
    {
        Assert.NotNull(Validate(Targeted() with { Sources = [Root("same", "past")],
            Expressions = [new(Root("same", "past"), "prefer keeping interruptions useful and brief")] }, Prior()));
        var old = Root("old", "past", -1);
        Assert.NotNull(Validate(Targeted() with { Sources = [old], Expressions = [new(old, "prefer keeping interruptions useful and brief")] }, Prior()));
        var early = Root("early", "future", -1);
        Assert.NotNull(Validate(Targeted() with { Sources = [early], Expressions = [new(early, "prefer keeping interruptions useful and brief")] }, Prior()));
    }

    [Fact] public void Enacted_promotion_requires_two_new_contexts()
    {
        var p = Targeted() with { Origin = UnderstandingOrigin.Enacted, Expressions = null };
        Assert.NotNull(Validate(p, Prior()));
        var second = Root("new2", "future2", 2);
        Assert.Null(Validate(p with { Sources = [Root(), second] }, Prior()));
        Assert.NotNull(Validate(p with { Sources = [Root(), second with { Scope = Root().Scope }] }, Prior()));
    }

    [Fact] public void Repeated_maintainer_inference_does_not_promote() => Assert.NotNull(Validate(Targeted() with {
        Origin = UnderstandingOrigin.SecondOrderInferred, Expressions = null }, Prior()));

    [Fact] public void Attach_evidence_preserves_existing_meaning_and_scope()
    {
        var prior = Prior(UnderstandingOrigin.Expressed, UnderstandingStatus.Tentative);
        Assert.Null(Validate(Targeted(), prior));
        Assert.NotNull(Validate(Targeted() with { Content = "I always interrupt." }, prior));
        Assert.NotNull(Validate(Targeted() with { Conditions = "Everywhere forever." }, prior));
        Assert.NotNull(Validate(Targeted() with { Status = UnderstandingStatus.Hypothesis }, prior));
    }

    [Fact] public void Revise_keeps_same_owner_kind_subject_and_exact_target()
    {
        Assert.Null(Validate(Targeted(UnderstandingWriteOperation.Revise) with { Content = "Useful deadline reminders are an exception." }, Prior()));
        Assert.NotNull(Validate(Targeted(UnderstandingWriteOperation.Revise) with { Kind = UnderstandingKind.CurrentInterest }, Prior()));
        Assert.NotNull(Validate(Targeted(UnderstandingWriteOperation.Revise) with { TargetRevision = 2 }, Prior()));
        Assert.NotNull(Validate(Targeted(UnderstandingWriteOperation.Revise) with { TargetId = null, TargetRevision = null }, Prior()));
    }

    [Fact] public void Retract_requires_target_and_referenced_counterevidence()
    {
        var p = Targeted(UnderstandingWriteOperation.Retract) with { Origin = UnderstandingOrigin.SecondOrderInferred,
            Status = UnderstandingStatus.Retracted, Expressions = null, Conflicts = [Root()] };
        Assert.Null(Validate(p, Prior()));
        Assert.NotNull(Validate(p with { Conflicts = null }, Prior()));
        Assert.NotNull(Validate(p with { Conflicts = [Root("not-read")] }, Prior()));
    }

    [Fact] public void Duplicate_create_prefers_existing_entry() => Assert.NotNull(Validate(Proposal() with {
        Content = " Keep interruptions useful and brief. " }, existing: [Prior()]));

    [Fact] public void Legacy_deserialization_does_not_invent_authorship_or_status()
    {
        var old = Prior() with { Origin = null, Status = null, Expressions = null };
        var json = JsonSerializer.Serialize(old);
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.EnumerateObject().Where(p => p.Name is not ("Origin" or "Status" or "Expressions" or "Conflicts" or "MaintenanceIntents" or "LastReviewedAt"))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var restored = JsonSerializer.Deserialize<UnderstandingEntry>(JsonSerializer.Serialize(values))!;
        Assert.Null(restored.Origin); Assert.Null(restored.Status); Assert.Null(restored.LastReviewedAt);
    }

    [Fact] public void Legacy_proposals_remain_compatible_but_new_kind_and_partial_metadata_are_denied()
    {
        var old = Proposal() with { Origin = null, Status = null, Expressions = null };
        Assert.Null(Validate(old));
        Assert.NotNull(Validate(old with { Kind = UnderstandingKind.Uncertainty }));
        Assert.NotNull(Validate(old with { Origin = UnderstandingOrigin.Expressed }));
        Assert.NotNull(Validate(old with { Operation = UnderstandingWriteOperation.Retract }));
        Assert.NotNull(Validate(old with { TargetId = "prior", TargetRevision = 1 }, Prior()));
    }

    [Fact] public void Legacy_or_external_target_cannot_be_relabelled_autonomous()
    {
        Assert.NotNull(Validate(Targeted(), Prior() with { Origin = null, Status = null }));
        Assert.NotNull(Validate(Targeted(), Prior(UnderstandingOrigin.ExternallySuggested)));
    }

    [Fact] public void Hypothesis_and_external_suggestions_never_project_even_when_approved()
    {
        Assert.False(UnderstandingWritePolicy.IsProjectable(Prior() with { Review = UnderstandingReview.Approved }));
        Assert.False(UnderstandingWritePolicy.IsProjectable(Prior(UnderstandingOrigin.ExternallySuggested, UnderstandingStatus.Tentative)));
        Assert.True(UnderstandingWritePolicy.IsProjectable(Prior(UnderstandingOrigin.Expressed, UnderstandingStatus.Tentative)));
    }

    [Fact] public void Merged_source_capacity_is_bounded()
    {
        var old = Prior() with { Sources = Enumerable.Range(0, 32).Select(i => Root("old" + i, "past", -1)).ToArray() };
        Assert.NotNull(Validate(Targeted(), old));
    }
}
