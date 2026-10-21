namespace FgoPet.Understanding.Contracts;

public enum ReviewBatch { Smoke, Formal, Repair }
public enum ReviewCallOutcome { ReservedOrUnknown, Completed, Failed, Unknown }
public sealed record ReviewBudgetApproval(string Id, string RouteHash, string ApprovalHash, bool HumanReviewConfirmed,
    int SmokeLimit = 24, int FormalLimit = 114, int RepairLimit = 114, int TotalLimit = 252);
public sealed record ReviewCallPlan(ReviewBatch Batch, string TrialId, string ScopeKey, string RequestId,
    string RouteHash, string Purpose, UnderstandingMode Mode, long PolicyGeneration, string InputHash, int EstimatedInputTokens);
public sealed record ReviewCallReservation(string RequestHash, int Attempt, int TotalUsed, int BatchUsed, int TrialUsed);
public sealed record ReviewCallRecord(string RequestHash, ReviewBatch Batch, string TrialHash, string ScopeHash,
    string Purpose, int Attempt, UnderstandingMode Mode, long PolicyGeneration, string InputHash, int EstimatedInputTokens,
    ReviewCallOutcome Outcome, int? InputTokens = null, int? OutputTokens = null);
public sealed record ReviewBudgetSnapshot(int TotalUsed, IReadOnlyDictionary<ReviewBatch, int> BatchUsed,
    IReadOnlyList<ReviewCallRecord> Calls);

public interface IReviewCallBudget
{
    ReviewCallReservation? TryReserve(ReviewCallPlan plan);
    void Complete(string requestHash, ReviewCallOutcome outcome, int? inputTokens = null, int? outputTokens = null);
}
public sealed record ReviewRequestLimits(int MaxInputTokens, int MaxOutputTokens)
{
    public static ReviewRequestLimits Legacy { get; } = new(16000, 2048);
}
public sealed record ReviewBudgetUse(IReviewCallBudget Budget, ReviewBatch Batch, string TrialId, string ScopeKey,
    ReviewRequestLimits? RequestLimits = null);
public interface IReviewBudgetAccess
{
    ReviewBudgetUse? Get(FgoPet.Core.Dialogue.ConversationScope scope);
}
