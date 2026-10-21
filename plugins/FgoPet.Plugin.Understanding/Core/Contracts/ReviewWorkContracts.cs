using FgoPet.Core.Dialogue;

namespace FgoPet.Understanding.Contracts;

public enum ReviewWorkState { Unprocessed, Running, ReviewedNotPersisted, ProposedPending, Applied, NoChange, Failed, Suppressed }
public sealed record ReviewWorkItem(string Id, ExperienceSourceHead Head, int StartScalar, int IncludedScalars);
public sealed record ReviewWorkBatch(string Id, ConversationScope Scope, long Generation, bool Manual,
    IReadOnlyList<ReviewWorkItem> Items, int Attempts = 0, long InputRevision = 0);
public sealed record ReviewWorkSummary(int PendingSources, int PendingScalars, int CompletedTurns,
    IReadOnlyList<(string Id, ReviewWorkState State)> Batches);

public interface IReviewModelFactory
{
    bool Available(ConversationScope scope);
    IReviewModel Create(ConversationScope scope, UnderstandingState state, IChatDispatchGate admission);
    Task<IUnderstandingRoutingPolicy?> ClassifyAsync(ReviewModelInput input, ReviewModelResult result,
        UnderstandingState state, IChatDispatchGate admission, CancellationToken token) => Task.FromResult<IUnderstandingRoutingPolicy?>(null);
}
