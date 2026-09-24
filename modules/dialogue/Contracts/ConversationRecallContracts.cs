using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Validation;

namespace FgoPet.Core.Dialogue;

public sealed record ConversationScope
{
    public ConversationScope(string servantId, string? projectId)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : Phase3Validation.Id(projectId, nameof(projectId), 128);
    }
    public string ServantId { get; }
    public string? ProjectId { get; }
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { ServantId, ProjectId }))));
}
public sealed record HistoryAnchor(string ConversationId, string MessageId, int Sequence, DateTimeOffset CreatedAtUtc);
public sealed record HistoryHit(HistoryAnchor Anchor, string Title, string Excerpt, bool IsTruncated);
public sealed record HistoryReadCursor(string ScopeKey, string ConversationId, int Sequence, int TextOffset);
public sealed record HistoryReadPage(IReadOnlyList<HistoryHit> Items, HistoryReadCursor? Next);
public interface IConversationRecallRepository
{
    IReadOnlyList<HistoryHit> Browse(ConversationScope scope, string excludedConversationId, int limit = 10);
    IReadOnlyList<HistoryHit> Discover(ConversationScope scope, string excludedConversationId, string query, int limit = 20);
    HistoryReadPage Read(ConversationScope scope, HistoryAnchor anchor, int maxChars = 6000, HistoryReadCursor? cursor = null);
}
public enum RecallStatus { Found, Empty, Ambiguous, Unavailable }
public sealed record ConversationRecallResult(RecallStatus Status, IReadOnlyList<HistoryHit> Sources, string? Clarification = null);
public interface IConversationRecall
{
    Task<ConversationRecallResult> RetrieveAsync(ConversationScope scope, string currentConversationId, string userMessage, CancellationToken cancellationToken);
}
