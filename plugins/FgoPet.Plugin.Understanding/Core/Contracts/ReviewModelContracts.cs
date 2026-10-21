using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Understanding.Contracts;

/// <summary>Host-read original material. Coverage describes the supplied slice, never a model claim.</summary>
public sealed record ReviewOriginal(ConversationSourceSnapshot Source, string Text, UnderstandingSourceCoverage Coverage);

/// <summary>Prepared by the owner after source, scope and policy validation. Contains no write authority.</summary>
public sealed record ReviewModelInput(string OperationId, ConversationScope Scope,
    IReadOnlyList<ReviewOriginal> Originals, IReadOnlyList<UnderstandingEntry> Targets,
    Action<CancellationToken> EnsureCurrent);

public sealed record ReviewModelResult(IReadOnlyList<UnderstandingProposal> Proposals,
    IReadOnlyList<UnderstandingSourceCoverage> SuppliedCoverage, ChatUsage Usage,
    TokenMeasurement Input, string RequestHash);

/// <summary>Produces candidates only. The owner retains permission, dispatch accounting and commit authority.</summary>
public interface IReviewModel
{
    Task<ReviewModelResult> ReviewAsync(ReviewModelInput input, CancellationToken cancellationToken = default);
}
