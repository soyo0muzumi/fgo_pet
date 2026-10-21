using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Understanding.Contracts;

public enum UnderstandingMode { Off, Proposals, Bounded }
public enum UnderstandingDomain { Self, User, World, Relationship }
public enum UnderstandingKind { CurrentInterest, CurrentStance, OpenQuestion, SelfReflection, ExternalObservation, Interpretation, UserFact, StableIdentity, Permission }
public enum UnderstandingReview { Unreviewed, Approved, Rejected }
public enum UnderstandingEffect { Pending, Bounded, Approved }
public enum UnderstandingLife { Active, Dormant, Resolved, Retracted }
public enum UnderstandingOutcome { Applied, ProposedPending, ReviewedNotPersisted, NoChange, Failed }
public enum UnderstandingAction { Approve, Reject, Suspend, Resolve, Delete, Correct, Restore }

public sealed record UnderstandingPolicy(UnderstandingMode Mode = UnderstandingMode.Off, bool Enabled = true,
    bool ReadSources = false, bool ExternalProcessing = false, bool SaveCandidates = false, bool UseInDialogue = false);
public sealed record UnderstandingState(UnderstandingPolicy Policy, long Revision, long Generation, long AutoSince);

/// <summary>Host-side proposal only; no model-facing write tool accepts this DTO.</summary>
public sealed record UnderstandingProposal(string Id, UnderstandingDomain Domain, UnderstandingKind Kind,
    string Subject, string Content, string Conditions, IReadOnlyList<ConversationSourceSnapshot> Sources,
    string? TargetId = null, int? TargetRevision = null);
public sealed record UnderstandingTicket(string Id, ConversationScope Scope, long Generation, long InputRevision,
    bool Manual, IReadOnlyList<ConversationSourceSnapshot> Sources, IReadOnlyList<UnderstandingSourceCoverage>? Coverage = null);
/// <summary>Host-bound coverage of material actually supplied, independent of source-version validation.</summary>
public sealed record UnderstandingSourceCoverage(ConversationSourceSnapshot Source, int StartScalar, int IncludedScalars, int TotalScalars)
{
    public bool Complete => StartScalar == 0 && IncludedScalars == TotalScalars;
    public static UnderstandingSourceCoverage FromWindow(ExperienceWindowItem item) => new(item.Source, 0, item.IncludedScalars, item.TotalScalars);
}
public sealed record UnderstandingEntry(string Id, ConversationScope Scope, int Revision, UnderstandingDomain Domain,
    UnderstandingKind Kind, string Subject, string Content, string Conditions, string BasisKind,
    IReadOnlyList<ConversationSourceSnapshot> Sources, UnderstandingReview Review, UnderstandingEffect Effect,
    UnderstandingLife Life, DateTimeOffset ObservedAt, DateTimeOffset ActiveUntil, long PolicyGeneration,
    string? ReplacesId = null, int? ReplacesRevision = null, string? Correction = null,
    IReadOnlyList<UnderstandingSourceCoverage>? Coverage = null);
public sealed record UnderstandingItemReceipt(string Id, UnderstandingOutcome Outcome, int? Revision = null, string? Code = null);
public sealed record UnderstandingReceipt(string OperationId, IReadOnlyList<UnderstandingItemReceipt> Items);
public sealed record UnderstandingProjection(long Revision, IReadOnlyList<UnderstandingEntry> Items, string? Code = null);
public sealed record UnderstandingDiagnostic(DateTimeOffset At, string Correlation, string Stage, string? Code = null);

/// <summary>Trusted host classification, independent of a proposal's self-reported domain/risk label.
/// M1's default is review-required. Future semantic classification requires separate M2 validation.</summary>
public interface IUnderstandingRoutingPolicy
{
    bool IsLowImpact(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> verifiedRoots);
}

public interface IUnderstandingReader
{
    UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null);
}
