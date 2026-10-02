using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Validation;

namespace FgoPet.Core.Memory;

public sealed record MemoryScope
{
    public MemoryScope(string servantId, string? projectId)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : Phase3Validation.Id(projectId, nameof(projectId));
    }
    public string ServantId { get; }
    public string? ProjectId { get; }
}
public enum MemoryEvidenceKind { UnknownLegacy, UserStatement, AssistantSuggestion, ConfirmedDecision }
public sealed record MemorySource
{
    public MemorySource(MemoryScope scope, string conversationId, string messageId, string sourceFingerprint,
        DateTimeOffset occurredAtUtc, MemoryEvidenceKind kind, string? projectLabel = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        MessageId = Phase3Validation.Id(messageId, nameof(messageId));
        SourceFingerprint = Phase3Validation.Id(sourceFingerprint, nameof(sourceFingerprint));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        OccurredAtUtc = occurredAtUtc;
        ProjectLabel = string.IsNullOrWhiteSpace(projectLabel) ? null : Phase3Validation.Text(projectLabel, nameof(projectLabel), 160);
    }
    public MemoryScope Scope { get; }
    public string ConversationId { get; }
    public string MessageId { get; }
    public string SourceFingerprint { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public MemoryEvidenceKind Kind { get; }
    public string? ProjectLabel { get; }
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new[] { Scope.ServantId, Scope.ProjectId, ConversationId, MessageId, SourceFingerprint }))));
}
public sealed record MemoryWriteTicket(string TicketId, string Generation, long MemoryRevision, MemorySource Source);
public sealed record MemoryProposal
{
    public MemoryProposal(string text, string? replacesMemoryId = null, int? expectedMemoryVersion = null)
    {
        Text = Phase3Validation.Text(text, nameof(text), 2000);
        if ((replacesMemoryId is null) != (expectedMemoryVersion is null) || expectedMemoryVersion is <= 0)
            throw new ArgumentException("Replacement requires an exact ID and positive version.");
        ReplacesMemoryId = replacesMemoryId is null ? null : Phase3Validation.Id(replacesMemoryId, nameof(replacesMemoryId));
        ExpectedMemoryVersion = expectedMemoryVersion;
    }
    public string Text { get; }
    public string? ReplacesMemoryId { get; }
    public int? ExpectedMemoryVersion { get; }
}
public enum MemoryStageStatus { Staged, Duplicate, Stale, Invalid, CapacityExceeded }
public sealed record MemoryStageResult(MemoryStageStatus Status, IReadOnlyList<string> CandidateIds);
public interface IMemoryCandidateSink
{
    MemoryWriteTicket Begin(MemorySource source);
    MemoryStageResult Stage(MemoryWriteTicket ticket, IReadOnlyList<MemoryProposal> proposals);
    void Abandon(MemoryWriteTicket ticket);
}
public interface IMemoryWriteLifetime
{
    void StartSession();
    void InvalidateWrites();
}
public sealed class MemoryReviewException(string message) : InvalidOperationException(message);
