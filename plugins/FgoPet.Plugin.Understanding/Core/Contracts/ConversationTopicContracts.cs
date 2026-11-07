using FgoPet.Core.Dialogue;

namespace FgoPet.Understanding.Contracts;

/// <summary>A short-lived topic extracted from a full conversation review, distinct from durable Self/Relationship proposals.</summary>
public sealed record ConversationTopic(
    string Id,
    string RoleId,
    string? ProjectId,
    string ConversationId,
    int Revision,
    string Topic,
    string? Correction,
    string Attribution,
    string Tense,
    IReadOnlyList<string> Sources,
    string BasisStatement,
    string? CounterEvidence,
    bool ResearchCandidate,
    string? ResearchRationale,
    DateTimeOffset ActiveUntil,
    DateTimeOffset ObservedAt,
    TopicMaterialScope ScopeKind = TopicMaterialScope.Interest);

/// <summary>Read-only access to active short-term topics for research and dialogue candidate selection.</summary>
public interface IShortTermTopicSource
{
    IReadOnlyList<ConversationTopic> GetActiveTopics(ConversationScope scope, string? conversationId = null);
}

/// <summary>Store for managing short-term conversation topics across review, research and chat.</summary>
public interface IShortTermTopicStore : IShortTermTopicSource
{
    void SaveTopics(ConversationScope scope, string conversationId, IReadOnlyList<ConversationTopic> topics);
    void RetractTopic(ConversationScope scope, string conversationId, string topicId);
    void DeleteScope(ConversationScope scope);
}
