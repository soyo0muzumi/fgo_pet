using FgoPet.Core.Dialogue;

namespace FgoPet.Understanding.Contracts;

public enum TopicMaterialScope { Interest, OpenQuestion, External }

/// <summary>Short-lived external context tied to an exact interest/question revision; never a World fact.
/// LastUsedAt only means offered to a completed turn, not proof that the model cited or used it.</summary>
public sealed record TopicMaterial(string Id, string TopicKey, int TopicRevision, TopicMaterialScope Scope,
    string Summary, string RelevanceReason, string SourceTitle, string SourceUrl,
    DateTimeOffset FetchedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt = null);

/// <summary>Local material storage only. The caller owns source, external-processing and dialogue-use admission.</summary>
public interface ITopicMaterialStore
{
    void Add(ConversationScope scope, TopicMaterial material);
    IReadOnlyList<TopicMaterial> GetActive(ConversationScope scope);
    void MarkUsed(ConversationScope scope, string materialId, DateTimeOffset usedAt);
    /// <summary>Charges a failed/cancelled attempt too; at most six attempts in a rolling day, five minutes apart.</summary>
    bool TryReserveResearch(ConversationScope scope, string topicKey, DateTimeOffset now, TimeSpan refreshAfter, int dailyLimit = 6);
    /// <summary>Read-only eligibility hint; dispatch must still atomically reserve.</summary>
    bool CanResearch(ConversationScope scope, string topicKey, DateTimeOffset now, TimeSpan refreshAfter, int dailyLimit = 6);
    void DeleteScope(ConversationScope scope);
}
