namespace FgoPet.Dialogue.Contracts;

/// <summary>A host-selected existing conversation. This value never grants access by itself.</summary>
public sealed record ExternalConversationBinding(string RoleId, string ConversationId, string? ProjectId);

public enum ExternalConversationStatus
{
    Completed,
    WaitingInput,
    WaitingApproval,
    Cancelled,
    Busy,
    ScopeChanged,
    InvalidInput,
    Unavailable,
    Failed,
}

public sealed record ExternalInputReference(string RunId, string RequestId, long Revision);
public sealed record ExternalInputQuestion(string Id, string Text, IReadOnlyList<string> Options, bool AllowMultiple);
public sealed record ExternalConversationInput(string RunId, string RequestId, long Revision,
    DateTimeOffset ExpiresAt, IReadOnlyList<ExternalInputQuestion> Questions)
{
    public ExternalInputReference Reference => new(RunId, RequestId, Revision);
}

public sealed record ExternalInputAnswer(string QuestionId, IReadOnlyList<int> SelectedOptionIndices, string? Text = null);
public sealed record ExternalInputReply(ExternalInputReference Reference, IReadOnlyList<ExternalInputAnswer> Answers);

/// <summary>Text contains a completed persisted answer or a validated clarification; reasoning and history are never returned.</summary>
public sealed record ExternalConversationResult(ExternalConversationStatus Status, string? Text = null,
    string? MessageId = null, ExternalConversationInput? Input = null, string? ErrorCode = null);

/// <summary>
/// A trusted local host's second input surface for the shared desktop conversation.
/// The host owns sender authentication, binding consent, transport deduplication and delivery.
/// Calls never select another role, open a conversation, confirm permissions or overwrite the desktop draft.
/// </summary>
public interface IExternalConversationGateway
{
    Task<ExternalConversationBinding?> CaptureCurrentBindingAsync(CancellationToken cancellationToken = default);
    Task<ExternalConversationResult> ExchangeTextAsync(ExternalConversationBinding binding, string text,
        CancellationToken cancellationToken = default);
    Task<ExternalConversationResult> SendAsync(ExternalConversationBinding binding, string text,
        CancellationToken cancellationToken = default);
    Task<ExternalConversationResult> AnswerAsync(ExternalConversationBinding binding, ExternalInputReply reply,
        CancellationToken cancellationToken = default);
    Task<ExternalConversationResult> CancelAsync(ExternalConversationBinding binding, ExternalInputReference reference,
        CancellationToken cancellationToken = default);
}
