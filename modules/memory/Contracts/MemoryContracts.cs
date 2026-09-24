
using FgoPet.Core.Validation;

namespace FgoPet.Core.Memory;

/// <summary>Legacy read adapter. Automatic writes use IMemoryCandidateSink exclusively.</summary>
public interface IConversationMemory : IMemoryRecall
{
    IReadOnlyList<StoredMemory> ListEnabledMemories(string servantId);
    MemoryRecallSnapshot IMemoryRecall.Query(MemoryScope scope, string query, int maxItems, int maxChars) =>
        new(0, ListEnabledMemories(scope.ServantId).Where(m => m.ProjectId is null || m.ProjectId == scope.ProjectId)
            .Take(Math.Min(maxItems, 8)).ToArray());
}

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
        MemoryCandidateStatus status = MemoryCandidateStatus.Pending,
        string? projectId = null, MemorySource? source = null, string? replacesMemoryId = null,
        int? expectedMemoryVersion = null)
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
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        ProjectId = new MemoryScope(servantId, projectId).ProjectId;
        if (source is not null && (source.Scope != new MemoryScope(servantId, ProjectId) ||
            source.ConversationId != conversationId || source.MessageId != sourceMessageId))
            throw new ArgumentException("Memory source must match candidate scope and origin.");
        Source = source;
        var proposal = new MemoryProposal(text, replacesMemoryId, expectedMemoryVersion);
        ReplacesMemoryId = proposal.ReplacesMemoryId;
        ExpectedMemoryVersion = proposal.ExpectedMemoryVersion;
    }

    public string CandidateId { get; }
    public string ServantId { get; }
    public string ConversationId { get; }
    public string Text { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? SourceMessageId { get; }
    public string? AppearanceId { get; }
    public MemoryCandidateStatus Status { get; }
    public string? ProjectId { get; }
    public MemorySource? Source { get; }
    public string? ReplacesMemoryId { get; }
    public int? ExpectedMemoryVersion { get; }
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
        string? sourceCandidateId = null, string? projectId = null, int version = 1,
        MemorySource? source = null, bool sourceAvailable = false)
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
        ProjectId = new MemoryScope(servantId, projectId).ProjectId;
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        if (source is not null && source.Scope != new MemoryScope(servantId, ProjectId))
            throw new ArgumentException("Memory source must match scope.");
        Version = version;
        Source = source;
        SourceAvailable = sourceAvailable;
    }

    public string MemoryId { get; }
    public string ServantId { get; }
    public string Text { get; }
    public bool IsEnabled { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string? SourceCandidateId { get; }
    public string? ProjectId { get; }
    public int Version { get; }
    public MemorySource? Source { get; }
    public bool SourceAvailable { get; }
}
