using FgoPet.Core.Dialogue;

namespace FgoPet.Core.Memory;

public enum MemoryCandidateStatus
{
    Pending,
    Approved,
    Rejected,
}

public enum MemoryReviewAction
{
    Approve,
    Reject,
    Edit,
    Disable,
    Delete,
}

public sealed record MemoryCandidate
{
    public MemoryCandidate(
        string candidateId,
        string servantId,
        string conversationId,
        string text,
        DateTimeOffset createdAtUtc,
        string? sourceMessageId = null,
        string? appearanceId = null,
        MemoryCandidateStatus status = MemoryCandidateStatus.Pending)
    {
        CandidateId = Phase3Validation.Id(candidateId, nameof(candidateId));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        Text = Phase3Validation.Text(text, nameof(text), 2_000);
        CreatedAtUtc = createdAtUtc;
        SourceMessageId = string.IsNullOrWhiteSpace(sourceMessageId)
            ? null
            : Phase3Validation.Id(sourceMessageId, nameof(sourceMessageId));
        AppearanceId = string.IsNullOrWhiteSpace(appearanceId)
            ? null
            : Phase3Validation.Id(appearanceId, nameof(appearanceId));
        Status = status;
    }

    public string CandidateId { get; }
    public string ServantId { get; }
    public string ConversationId { get; }
    public string Text { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? SourceMessageId { get; }
    public string? AppearanceId { get; }
    public MemoryCandidateStatus Status { get; }
}

public sealed record StoredMemory
{
    public StoredMemory(
        string memoryId,
        string servantId,
        string text,
        bool isEnabled,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        string? sourceCandidateId = null)
    {
        MemoryId = Phase3Validation.Id(memoryId, nameof(memoryId));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        Text = Phase3Validation.Text(text, nameof(text), 2_000);
        IsEnabled = isEnabled;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        SourceCandidateId = string.IsNullOrWhiteSpace(sourceCandidateId)
            ? null
            : Phase3Validation.Id(sourceCandidateId, nameof(sourceCandidateId));
    }

    public string MemoryId { get; }
    public string ServantId { get; }
    public string Text { get; }
    public bool IsEnabled { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string? SourceCandidateId { get; }
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
