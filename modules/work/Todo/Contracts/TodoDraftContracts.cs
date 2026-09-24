using FgoPet.Core.Todo;

namespace FgoPet.App.Dialogue;

/// <summary>Work owns session drafts and confirmation. Conversation IDs are opaque scope keys.</summary>
public interface ITodoDraftWorkflow
{
    PendingTodoDraft Replace(string conversationId, string servantId, IReadOnlyList<TodoProposal> proposals);
    PendingTodoDraft? Get(string conversationId, string servantId);
    string? GetPromptState(string conversationId, string servantId);
    void ClearServant(string servantId);
    TodoDraftResult Cancel(string conversationId, string servantId, string? draftId = null);
    TodoDraftResult Confirm(string conversationId, string servantId, string draftId, int version, string idempotencyKey);
}

public enum TodoDraftStatus
{
    Pending,
    Confirming,
    Committed,
    Cancelled,
    Unknown,
}

public sealed record PendingTodoDraft(
    string DraftId,
    string ConversationId,
    string ServantId,
    int Version,
    IReadOnlyList<TodoProposal> Proposals,
    TodoDraftStatus Status = TodoDraftStatus.Pending,
    string? CreatedTodoId = null);

public enum TodoDraftResultKind
{
    Replaced,
    Committed,
    AlreadyCommitted,
    Cancelled,
    Stale,
    Unknown,
    Failed,
}

public sealed record TodoDraftResult(
    TodoDraftResultKind Kind,
    PendingTodoDraft? Draft = null,
    TodoItem? Todo = null,
    IReadOnlyList<TodoItem>? CreatedTodos = null)
{
    /// <summary>Every Todo created by this confirmation, with Todo retained as the first-item compatibility shortcut.</summary>
    public IReadOnlyList<TodoItem> Todos => CreatedTodos ?? (Todo is null ? Array.Empty<TodoItem>() : [Todo]);
}
