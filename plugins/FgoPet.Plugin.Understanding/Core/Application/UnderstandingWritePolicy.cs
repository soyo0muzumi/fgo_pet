using System.Runtime.CompilerServices;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;

[assembly: InternalsVisibleTo("FgoPet.Plugin.Understanding.Tests")]

namespace FgoPet.Understanding;

/// <summary>Structural authorship and recurrence checks; semantic entailment remains independently classified.</summary>
internal static class UnderstandingWritePolicy
{
    internal static bool IsAllowedKind(UnderstandingDomain domain, UnderstandingKind kind) =>
        domain == UnderstandingDomain.Self && kind is UnderstandingKind.CurrentStance or UnderstandingKind.CurrentInterest or
            UnderstandingKind.Uncertainty or UnderstandingKind.InteractionTendency or UnderstandingKind.SelfObservation ||
        domain == UnderstandingDomain.Relationship && kind == UnderstandingKind.InteractionPattern;

    /// <summary>Legacy entries keep their existing review/effect policy; unknown new metadata never gains that fallback.</summary>
    internal static bool IsProjectable(UnderstandingEntry entry) =>
        entry.Origin is null && entry.Status is null && entry.Kind < UnderstandingKind.Uncertainty ||
        entry.Origin is UnderstandingOrigin.Expressed or UnderstandingOrigin.Enacted or UnderstandingOrigin.SecondOrderInferred &&
        entry.Status is UnderstandingStatus.Tentative or UnderstandingStatus.Established;

    internal static string? Validate(UnderstandingTicket ticket, UnderstandingProposal proposal,
        IReadOnlyList<UnderstandingEntry> existing, IReadOnlyList<ReviewOriginal> originals, DateTimeOffset now)
    {
        var p = proposal;
        if (p.Origin is null && p.Status is null)
            return p.Kind >= UnderstandingKind.Uncertainty || p.Operation != UnderstandingWriteOperation.Create ||
                p.Expressions is not null || p.Conflicts is not null || p.MaintenanceIntents is not null ||
                p.TargetId is not null && existing.Any(e => e.Id == p.TargetId && (e.Origin is not null || e.Status is not null))
                ? "UNDERSTANDING_WRITE_METADATA_REQUIRED" : null;
        if (p.Origin is null || p.Status is null || !Enum.IsDefined(p.Origin.Value) || !Enum.IsDefined(p.Status.Value) ||
            !Enum.IsDefined(p.Operation)) return "UNDERSTANDING_WRITE_INVALID_METADATA";
        if (!IsAllowedKind(p.Domain, p.Kind) || p.Domain == UnderstandingDomain.Self && p.Subject != "assistant")
            return "UNDERSTANDING_WRITE_SCOPE_DENIED";
        if (p.Sources is null || p.Sources.Count is < 1 or > 32 || p.Sources.Any(s => s is null || !ticket.Sources.Contains(s)) ||
            p.Sources.Distinct().Count() != p.Sources.Count || string.IsNullOrWhiteSpace(p.Content) || string.IsNullOrWhiteSpace(p.Conditions))
            return "UNDERSTANDING_WRITE_INVALID_EVIDENCE";
        if (p.Status == UnderstandingStatus.Established || p.Status == UnderstandingStatus.Dormant ||
            (p.Status == UnderstandingStatus.Retracted) != (p.Operation == UnderstandingWriteOperation.Retract))
            return "UNDERSTANDING_WRITE_STATUS_DENIED";
        if (p.MaintenanceIntents is { } intents && (intents.Count > 4 || intents.Any(i => !Enum.IsDefined(i)) || intents.Distinct().Count() != intents.Count))
            return "UNDERSTANDING_WRITE_INVALID_METADATA";

        UnderstandingEntry? target = null;
        if (p.Operation != UnderstandingWriteOperation.Create)
        {
            target = existing.FirstOrDefault(e => e.Scope == ticket.Scope && e.Id == p.TargetId && e.Revision == p.TargetRevision);
            if (target is null || target.Life == UnderstandingLife.Retracted || target.Status == UnderstandingStatus.Retracted)
                return "UNDERSTANDING_STALE";
            if (target.Origin is null || target.Status is null) return "UNDERSTANDING_WRITE_LEGACY_TARGET";
            if (target.ProposedOperation == UnderstandingWriteOperation.Retract) return "UNDERSTANDING_WRITE_WITHDRAWAL_REVIEW_REQUIRED";
            if (target.Domain != p.Domain || target.Kind != p.Kind || target.Subject != p.Subject)
                return "UNDERSTANDING_WRITE_TARGET_MISMATCH";
            if (target.Sources.Concat(p.Sources).Distinct().Count() > 32) return "UNDERSTANDING_WRITE_SOURCE_LIMIT";
        }
        else
        {
            if (p.TargetId is not null || p.TargetRevision is not null) return "UNDERSTANDING_WRITE_TARGET_MISMATCH";
            if (existing.Any(e => e.Scope == ticket.Scope && e.Life != UnderstandingLife.Retracted && e.Domain == p.Domain &&
                e.Kind == p.Kind && e.Subject == p.Subject && Normalize(e.Correction ?? e.Content) == Normalize(p.Content)))
                return "UNDERSTANDING_WRITE_DUPLICATE";
        }

        if (p.Conflicts is { } conflicts && (conflicts.Count > 32 || conflicts.Any(s => s is null ||
            !ticket.Sources.Contains(s) && target?.Sources.Contains(s) != true) || conflicts.Distinct().Count() != conflicts.Count))
            return "UNDERSTANDING_WRITE_INVALID_CONFLICT";
        if (p.Expressions is { } expressions && (expressions.Count > 32 || expressions.Any(e => e is null ||
            e.Source is null || !p.Sources.Contains(e.Source) || string.IsNullOrWhiteSpace(e.Quote) || e.Quote.Length > 2000 ||
            !CompleteAssistant(e.Source) || !originals.Any(o => o.Source == e.Source && o.Text.Contains(e.Quote, StringComparison.Ordinal)))))
            return "UNDERSTANDING_WRITE_INVALID_EXPRESSION";

        if (p.Operation == UnderstandingWriteOperation.Retract)
        {
            if (p.Origin != target!.Origin || p.Conflicts is not { Count: > 0 }) return "UNDERSTANDING_WRITE_RETRACTION_UNSUPPORTED";
            return null;
        }
        var promotion = target?.Status == UnderstandingStatus.Hypothesis && p.Status == UnderstandingStatus.Tentative;
        if (p.Operation == UnderstandingWriteOperation.AttachEvidence &&
            (p.Content != target!.Content || p.Conditions != target.Conditions || p.Status != target.Status && !promotion))
            return "UNDERSTANDING_WRITE_ATTACH_CHANGED_MEANING";
        if (p.Origin == UnderstandingOrigin.ExternallySuggested && p.Status != UnderstandingStatus.Hypothesis ||
            target?.Origin == UnderstandingOrigin.ExternallySuggested && p.Origin != UnderstandingOrigin.ExternallySuggested)
            return "UNDERSTANDING_WRITE_EXTERNAL_SELF_DENIED";
        if (target is not null && p.Origin != target.Origin && !promotion) return "UNDERSTANDING_WRITE_AUTHORSHIP_CHANGED";
        if (p.Origin == UnderstandingOrigin.Expressed && p.Expressions is not { Count: > 0 })
            return "UNDERSTANDING_WRITE_EXPRESSION_REQUIRED";
        if (p.Origin is UnderstandingOrigin.Enacted or UnderstandingOrigin.SecondOrderInferred &&
            p.Status != UnderstandingStatus.Hypothesis && !promotion && target?.Status != UnderstandingStatus.Tentative)
            return "UNDERSTANDING_WRITE_INFERENCE_ONLY";
        if (promotion)
        {
            var independent = p.Sources.Where(s => !target!.Sources.Contains(s) && s.OccurredAtUtc > target.ObservedAt &&
                s.OccurredAtUtc <= now && target.Sources.All(old => old.Scope.ConversationId != s.Scope.ConversationId) && CompleteAssistant(s)).ToArray();
            var expressed = p.Origin == UnderstandingOrigin.Expressed && p.Expressions?.Any(e => independent.Contains(e.Source)) == true;
            var enacted = p.Origin == UnderstandingOrigin.Enacted && independent.Select(s => s.Scope.ConversationId).Distinct().Count() >= 2;
            if (!expressed && !enacted) return "UNDERSTANDING_WRITE_INDEPENDENT_RECURRENCE_REQUIRED";
        }
        return null;

        bool CompleteAssistant(ConversationSourceSnapshot source)
        {
            if (source.Kind != ConversationSourceKind.Assistant) return false;
            var original = originals.FirstOrDefault(o => o is not null && o.Source == source && o.Coverage is not null &&
                o.Coverage.Source == source && o.Coverage.Complete && o.Coverage.IncludedScalars > 0 &&
                o.Text is not null && o.Text.EnumerateRunes().Count() == o.Coverage.IncludedScalars);
            return original is not null && ticket.Coverage?.Any(c => c == original.Coverage && c.Complete) == true;
        }
    }

    private static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
