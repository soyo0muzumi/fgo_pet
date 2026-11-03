using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    // Called only after the whole proposal, original read set and exact target revision were validated.
    private UnderstandingEntry? PrepareWrite(UnderstandingTicket ticket, UnderstandingProposal proposal,
        UnderstandingEntry? target, UnderstandingState state, bool supported)
    {
        var now = _clock.GetUtcNow();
        var sources = (target?.Sources ?? []).Concat(proposal.Sources).Distinct().ToArray();
        var coverage = (target?.Coverage ?? []).Concat(ticket.Coverage?.Where(c => proposal.Sources.Contains(c.Source)) ?? [])
            .GroupBy(c => c.Source).Select(g => g.OrderByDescending(c => c.IncludedScalars).First()).ToArray();
        var expressions = (target?.Expressions ?? []).Concat(proposal.Expressions ?? []).Distinct().ToArray();
        var conflicts = (target?.Conflicts ?? []).Concat(proposal.Conflicts ?? []).Distinct().ToArray();
        var intents = (target?.MaintenanceIntents ?? []).Concat(proposal.MaintenanceIntents ?? []).Distinct().ToArray();
        var bounded = state.Policy.Mode == UnderstandingMode.Bounded && supported &&
            proposal.Status == UnderstandingStatus.Tentative && proposal.Origin != UnderstandingOrigin.ExternallySuggested &&
            proposal.Sources.All(s => coverage.Any(c => c.Source == s && c.IncludedScalars > 0));
        if (proposal.Operation == UnderstandingWriteOperation.AttachEvidence && target is not null &&
            target.Sources.SequenceEqual(sources) && (target.Expressions ?? []).SequenceEqual(expressions) &&
            (target.Conflicts ?? []).SequenceEqual(conflicts) && (target.MaintenanceIntents ?? []).SequenceEqual(intents) &&
            target.Status == proposal.Status && target.Origin == proposal.Origin) return null;

        // A semantically unapproved mutation of an effective view is a separate pending replacement.
        // Attaching to a pending hypothesis cannot affect first-order context.
        var retractRequested = proposal.Operation == UnderstandingWriteOperation.Retract;
        var retracted = retractRequested && state.Policy.Mode == UnderstandingMode.Bounded && supported;
        var inPlace = target is not null && (bounded || retracted || !retractRequested &&
            target.Effect == UnderstandingEffect.Pending && target.Review == UnderstandingReview.Unreviewed);
        // Preserve a pending replacement's dependency on its still-effective predecessor.
        var replacement = inPlace && target!.ReplacesId is { } prior && prior != target.Id ? prior : target?.Id;
        var replacementRevision = inPlace && target!.ReplacesId is { } previous && previous != target.Id
            ? target.ReplacesRevision : target?.Revision;
        return new(inPlace ? target!.Id : proposal.Id, ticket.Scope, inPlace ? target!.Revision + 1 : 1,
            proposal.Domain, proposal.Kind, proposal.Subject, proposal.Content, proposal.Conditions,
            proposal.Origin!.Value.ToString(), sources, UnderstandingReview.Unreviewed,
            bounded || retracted ? UnderstandingEffect.Bounded : UnderstandingEffect.Pending,
            retracted ? UnderstandingLife.Retracted : UnderstandingLife.Active,
            target?.ObservedAt ?? now,
            proposal.Domain is UnderstandingDomain.Self or UnderstandingDomain.Relationship ? now.AddDays(7) : DateTimeOffset.MaxValue,
            state.Generation, replacement, replacementRevision,
            Correction: proposal.Operation == UnderstandingWriteOperation.AttachEvidence ? target?.Correction : null,
            Coverage: coverage.Length == 0 ? null : coverage, Origin: proposal.Origin, Status: retractRequested && !retracted ? target!.Status : proposal.Status, Expressions: expressions,
            Conflicts: conflicts, MaintenanceIntents: intents, LastReviewedAt: now,
            ProposedOperation: retractRequested && !retracted ? UnderstandingWriteOperation.Retract : null);
    }
}
