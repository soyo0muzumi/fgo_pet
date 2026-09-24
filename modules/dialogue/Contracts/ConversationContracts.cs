using FgoPet.Core.Memory;
using FgoPet.Core.Validation;

namespace FgoPet.Core.Dialogue;

/// <summary>Metadata only. Opening a conversation is a separate full-message query.</summary>
public interface IConversationHistoryQuery
{
    ConversationHistoryPage ReadPage(string servantId, int pageSize = 50, ConversationHistoryCursor? before = null);
    ConversationHistoryPage ReadPage(ConversationScope scope, int pageSize = 50, ConversationHistoryCursor? before = null);
}

/// <summary>Read-only raw dialogue access for source validation; no storage implementation leaks through this port.</summary>
public interface IConversationReader
{
    Conversation? GetConversation(string conversationId, string servantId);
    IReadOnlyList<ChatMessage> LoadMessages(string conversationId, string servantId);
}

/// <summary>
/// Application-facing conversation operations. Summary projections keep their own
/// IConversationContextStore contract; SQL connections and backup operations are not exposed.
/// Implementations must enforce servant ownership and validate recall sources within the supplied scope.
/// </summary>
public interface IConversationStore : IConversationReader, IConversationHistoryQuery
{
    bool Exists(string conversationId, string servantId);
    IReadOnlyList<Conversation> ListConversations(string servantId);
    Conversation CreateConversation(string conversationId, string servantId, ContentContextKey contentContext,
        DateTimeOffset createdAtUtc, string? projectId = null, string? projectLabel = null);
    void Append(ChatMessage message);
    bool IsCurrentSource(ConversationScope scope, HistoryHit source);
    // Deletion and removal of the matching active-conversation state must be atomic.
    void DeleteConversation(string conversationId, string servantId, string? activeStateKey = null);
    string? ReadState(string key);
    void WriteState(string key, string value, DateTimeOffset updatedAtUtc);
    void DeleteState(string key);
}

public sealed record ConversationHistoryEntry(string ConversationId, string Title, DateTimeOffset UpdatedAtUtc, bool IsArchived);
// Preserve the storage sort key verbatim; reformatting older UTC strings can skip a page.
public sealed record ConversationHistoryCursor(string ServantId, string UpdatedAtSortKey, string ConversationId, string? ScopeKey = null);
public sealed record ConversationHistoryPage(IReadOnlyList<ConversationHistoryEntry> Items, ConversationHistoryCursor? Next);

public enum ChatMessageRole
{
    System,
    User,
    Assistant,
}

public enum ChatMessageStatus
{
    Pending,
    Completed,
    Cancelled,
    Failed,
}

public enum ConversationSendStatus
{
    Completed,
    Cancelled,
    Failed,
    ConfigurationRequired,
}

public sealed record Conversation
{
    public Conversation(
        string conversationId,
        string servantId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        ContentContextKey contentContext,
        bool isArchived = false,
        string? projectId = null,
        string? projectLabel = null)
    {
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        IsArchived = isArchived;
        ProjectId = new ConversationScope(servantId, projectId).ProjectId;
        ProjectLabel = string.IsNullOrWhiteSpace(projectLabel) ? null : Phase3Validation.Text(projectLabel, nameof(projectLabel), 160);
    }

    public string ConversationId { get; }
    public string ServantId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public ContentContextKey ContentContext { get; }
    public bool IsArchived { get; }
    public string? ProjectId { get; }
    public string? ProjectLabel { get; }
}

public sealed record ChatMessage
{
    public ChatMessage(
        string messageId,
        string conversationId,
        string servantId,
        ChatMessageRole role,
        string text,
        ChatMessageStatus status,
        DateTimeOffset createdAtUtc,
        ContentContextKey contentContext,
        int sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        MessageId = Phase3Validation.Id(messageId, nameof(messageId));
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        Text = status is ChatMessageStatus.Cancelled or ChatMessageStatus.Failed
            ? Phase3Validation.OptionalText(text, nameof(text), 12_000)
            : Phase3Validation.Text(text, nameof(text), 12_000);
        Role = role;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Sequence = sequence;
    }

    public string MessageId { get; }
    public string ConversationId { get; }
    public string ServantId { get; }
    public ChatMessageRole Role { get; }
    public string Text { get; }
    public ChatMessageStatus Status { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public ContentContextKey ContentContext { get; }
    public int Sequence { get; }
}

public sealed record ConversationSendResult(
    ConversationSendStatus Status,
    string ConversationId,
    string? AssistantMessageId = null,
    string? SafeError = null);

public enum ConversationUpdateType
{
    HistorySources,
    UserMessagePersisted,
    RequestStage,
    AssistantDelta,
    AssistantCompleted,
    Cancelled,
    Failed,
}

public enum ConversationRequestStage
{
    Preparing,
    Compacting,
    RequestStarted,
    ResponseHeadersReceived,
    StreamingReasoning,
    StreamingTool,
    StreamingAnswer,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Outcome of the submit_todo_proposals tool-call channel for the UI.</summary>
public enum TodoToolCallOutcome
{
    None,
    ProposalsReady,
    InvalidToolCall,
    NoProposal,
    TextFallback,
    PendingDraftReplaced,
    ConfirmationUnknown,
    Cancelled,
    Confirmed,
    CommitUnknown,
}

public sealed record ConversationUpdate(
    ConversationUpdateType Type,
    string ConversationId,
    string? MessageId = null,
    string? TextDelta = null,
    string? SafeError = null,
    string? ServantId = null,
    string? StructuredResponse = null,
    TodoToolCallOutcome TodoOutcome = TodoToolCallOutcome.None,
    string? TodoDetail = null,
    string? ReasoningDelta = null,
    ConversationRequestStage? RequestStage = null,
    int? HttpStatusCode = null,
    string? ProviderErrorCode = null,
    FgoPet.Core.Portraits.ExpressionSemantic? Expression = null,
    string? TodoDraftId = null,
    int? TodoDraftVersion = null,
    string? CreatedTodoId = null,
    IReadOnlyList<HistoryHit>? HistorySources = null);

public sealed record ChatRequest
{
    public ChatRequest(
        string servantId,
        string conversationId,
        IReadOnlyList<PromptMessage> messages,
        ContentContextKey? contentContext = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<ChatToolDefinition>? tools = null,
        string? toolChoice = null,
        int? maxOutputTokens = null)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        if (maxOutputTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        MaxOutputTokens = maxOutputTokens;
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        if (Messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        ContentContext = contentContext;
        Metadata = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        Tools = tools is null ? null : tools.ToArray();
        if (Tools is { Count: 0 })
        {
            throw new ArgumentException("Tools collection must be null or non-empty.", nameof(tools));
        }

        ToolChoice = string.IsNullOrWhiteSpace(toolChoice)
            ? null
            : Phase3Validation.Id(toolChoice, nameof(toolChoice), 64);
        if (ToolChoice is not null && Tools is null)
        {
            throw new ArgumentException("ToolChoice requires Tools to be set.", nameof(toolChoice));
        }
    }

    public string ServantId { get; }
    public string ConversationId { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public ContentContextKey? ContentContext { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
    public IReadOnlyList<ChatToolDefinition>? Tools { get; }
    public string? ToolChoice { get; }
    public int? MaxOutputTokens { get; }
}

public sealed record ChatStreamChunk(string TextDelta, bool IsComplete = false, string? FinishReason = null, ChatToolCallDelta? ToolCallDelta = null, string? ReasoningDelta = null, ChatUsage? Usage = null)
{
    // Streaming fragments are concatenated as-is: never trim them or a provider that
    // splits on word boundaries loses every space between English words.
    public string TextDelta { get; } = Phase3Validation.Fragment(TextDelta, nameof(TextDelta), 4_096);
    public string? FinishReason { get; } = string.IsNullOrWhiteSpace(FinishReason)
        ? null
        : Phase3Validation.Id(FinishReason, nameof(FinishReason), 64);
    public ChatToolCallDelta? ToolCallDelta { get; } = ToolCallDelta;
    public string? ReasoningDelta { get; } = ReasoningDelta is null
        ? null
        : Phase3Validation.Fragment(ReasoningDelta, nameof(ReasoningDelta), 4_096);
}

public sealed record ChatCompletion(
    string Text,
    string? Emotion = null,
    string? FeedbackType = null,
    MemoryCandidate? MemoryCandidate = null)
{
    public string Text { get; } = Phase3Validation.Text(Text, nameof(Text), 12_000);
    public string? Emotion { get; } = string.IsNullOrWhiteSpace(Emotion)
        ? null
        : Phase3Validation.Id(Emotion, nameof(Emotion), 64);
    public string? FeedbackType { get; } = string.IsNullOrWhiteSpace(FeedbackType)
        ? null
        : Phase3Validation.Id(FeedbackType, nameof(FeedbackType), 64);
}
