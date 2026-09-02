using FgoPet.Core.Memory;

namespace FgoPet.Core.Dialogue;

internal static class Phase3Validation
{
    public static string Id(string value, string parameterName, int maxLength = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string Text(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string OptionalText(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Text(value, parameterName, maxLength);
    }
}

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

public sealed record ContentContextKey
{
    public ContentContextKey(
        string servantId,
        string packageId,
        string packageVersion,
        string appearanceId,
        string personaVersion,
        string knowledgeVersion)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        PackageId = Phase3Validation.Id(packageId, nameof(packageId));
        PackageVersion = Phase3Validation.Id(packageVersion, nameof(packageVersion), 64);
        AppearanceId = Phase3Validation.Id(appearanceId, nameof(appearanceId));
        PersonaVersion = Phase3Validation.Id(personaVersion, nameof(personaVersion), 64);
        KnowledgeVersion = Phase3Validation.Id(knowledgeVersion, nameof(knowledgeVersion), 64);
    }

    public string ServantId { get; }
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string AppearanceId { get; }
    public string PersonaVersion { get; }
    public string KnowledgeVersion { get; }
}

public sealed record Conversation
{
    public Conversation(
        string conversationId,
        string servantId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        ContentContextKey contentContext,
        bool isArchived = false)
    {
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        IsArchived = isArchived;
    }

    public string ConversationId { get; }
    public string ServantId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public ContentContextKey ContentContext { get; }
    public bool IsArchived { get; }
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
    UserMessagePersisted,
    AssistantDelta,
    AssistantCompleted,
    Cancelled,
    Failed,
}

public sealed record ConversationUpdate(
    ConversationUpdateType Type,
    string ConversationId,
    string? MessageId = null,
    string? TextDelta = null,
    string? SafeError = null,
    string? ServantId = null,
    string? StructuredResponse = null);

public sealed record ChatRequest
{
    public ChatRequest(
        string servantId,
        string conversationId,
        IReadOnlyList<PromptMessage> messages,
        ContentContextKey? contentContext = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        ConversationId = Phase3Validation.Id(conversationId, nameof(conversationId));
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        if (Messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        ContentContext = contentContext;
        Metadata = metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
    }

    public string ServantId { get; }
    public string ConversationId { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public ContentContextKey? ContentContext { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

public sealed record ChatStreamChunk(string TextDelta, bool IsComplete = false, string? FinishReason = null)
{
    public string TextDelta { get; } = Phase3Validation.OptionalText(TextDelta, nameof(TextDelta), 4_096);
    public string? FinishReason { get; } = string.IsNullOrWhiteSpace(FinishReason)
        ? null
        : Phase3Validation.Id(FinishReason, nameof(FinishReason), 64);
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
