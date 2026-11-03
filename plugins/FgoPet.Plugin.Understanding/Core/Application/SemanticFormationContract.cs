using System.Security.Cryptography;
using System.Text;

namespace FgoPet.Understanding;

/// <summary>Shared model-facing formation criteria; host authority and structural gates remain separate.</summary>
internal static class SemanticFormationContract
{
    internal const string Rules = """
        Typed formation criteria for Self CurrentStance and CurrentInterest (apply in addition to all authorship and status gates):
        Supporting first-order evidence must intersect on the same narrow conditional meaning, not a union of different reasons,
        shared nouns or cosmetic behavior. Distinguish independent situations from same-task continuation, episodic recall and
        repeated maintenance. conversationGroup groups source turns from one conversation; grouping alone does not prove independence.
        CurrentStance is a reproducible conditional judgement or action: IF conditions THEN tendency BECAUSE reason.
        Cross-context clarification between independently plausible alternatives without urgency may yield predictive surplus beyond
        general honesty. Preserve urgency, available evidence, domain and action limits; never generalize into personality.
        Preference concerns relative choices among alternatives; forced or elicited choices alone are preference clues, not CurrentInterest.
        CurrentInterest needs sustained attention or information-seeking after the initiating cue has disappeared: at least two independent subsequent behavioral observations,
        at least one a cue-independent return or connection, or costly information-seeking act. Exposure and verbal curiosity do not count.
        Two acts must concern the same specific question or topic and reason; a single later act is eligible for review only, not Tentative.
        User, world, conversation and persona seeds may be origins; later independently experience-maintained attention may qualify.
        Immediate compliance with an assigned interest is not independent maintenance. Preserve persona-seeded provenance explicitly in
        content or conditions. Persona instantiation alone is no-op; a narrower conditional rule with explanatory or predictive surplus
        may be an elaboration, with authorship and provenance preserved.
        Boundary calibration for every proposal: generalize minimally. State the conditions that the shared reason actually requires, and
        drop scene-only condition such as a specific deadline value, a named artefact, a place or one meeting procedure unless that detail is
        itself part of the reason. A deadline appearing in one scene is not by itself evidence of a general urgency rule.
        Surface similarity with different reasons: never merge by OR or by shared nouns. When the underlying reason or causal mechanism
        differs, do not lift different reasons into one higher covering principle; prefer no-op or a separately bounded entry, even when the
        surface action looks identical.
        AttachEvidence must carry the whole substantive meaning of the target and may extend its conditions only with supporting evidence;
        it must not silently narrow or broaden the existing boundary, and it must not restate a scene detail as a general rule.
        Before AttachEvidence: the new source must directly support at least one existing reason, condition, or behavioral claim
        already represented by the target. If it only introduces a different rationale, context, or behavioral pattern, do not attach it
        merely because the outcomes or surface structure are similar. Use Create / Revise / NoChange instead.
        Evidence compatibility invariant for Create, AttachEvidence and Revise: every source cited as support must be compatible with
        the resulting entry's specific reason, required conditions and behavioral claim; the sources together must support its whole substantive meaning.
        For Revise, recheck compatibility against the revised meaning, not merely the old target; rewording or preserving provenance cannot
        turn different reasons into shared support. Retained historical sources are provenance, not automatic evidence for the revised claim.
        If support covers only one part, specialize to that supported meaning or use a separately bounded entry / NoChange;
        never keep an unsupported cross-domain union or replace it with a vacuous covering principle.
        Do not overcorrect into silence: unnecessary NoChange is a failure too. When evidence supports a bounded conditional pattern,
        failing to form it hides a real regularity; only when the evidence is insufficient is no-op correct.
        Do not attribute to the assistant any observation not present in the raw sources, and do not present an inferred mechanism as if it
        were stated or enacted.
        Ask: Independent? Same why? Beyond persona? Predictive and falsifiable? Self relevant? Capability statements, advice,
        task procedures, politeness and forced choices alone do not satisfy formation. If evidence is insufficient, prefer no-op or review.
        """;

    internal static string ConversationGroup(string conversationId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationId)));
}
