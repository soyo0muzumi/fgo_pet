using FgoPet.Core.Validation;

namespace FgoPet.Core.Dialogue;

public sealed record ConversationContextSnapshot(ConversationScope Scope, string ConversationId,
    long ContextRevision, long ProjectionRevision, ConversationSummary? Summary,
    IReadOnlyList<ChatMessage> UncoveredMessages, string PrefixFingerprint);
public sealed record CompactionCommit(ConversationContextSnapshot Source, ConversationSummary Summary,
    ModelRouteKey Route, int InputTokensBefore, int InputTokensAfter);
public interface IConversationContextStore
{
    ConversationContextSnapshot Read(ConversationScope scope, string conversationId);
    bool TryCommit(CompactionCommit commit);
}
public sealed record SummaryAttempt(string Text, string? FinishReason, ChatUsage? Usage);
public interface IConversationSummarizer
{
    Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken);
}
public sealed record ConversationSummary
{
    public ConversationSummary(
        string summaryId,
        string conversationId,
        string servantId,
        string summaryText,
        int coveredThroughSequence,
        string coveredThroughMessageId,
        ContentContextKey contentContext,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (coveredThroughSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredThroughSequence));
        }

        SummaryId = Phase3Validation.Id(summaryId, nameof(summaryId));
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        SummaryText = Phase3Validation.Text(summaryText, nameof(summaryText), 6_000);
        CoveredThroughSequence = coveredThroughSequence;
        CoveredThroughMessageId = Phase3Validation.Id(coveredThroughMessageId, nameof(coveredThroughMessageId));
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string SummaryId { get; }
    public string ConversationId { get; }
    public string ServantId { get; }
    public string SummaryText { get; }
    public int CoveredThroughSequence { get; }
    public string CoveredThroughMessageId { get; }
    public ContentContextKey ContentContext { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
}
