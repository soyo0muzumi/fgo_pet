using System.Collections.Concurrent;
using FgoPet.Core.Todo;

namespace FgoPet.App.Dialogue;

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

public sealed record TodoDraftResult(TodoDraftResultKind Kind, PendingTodoDraft? Draft = null, TodoItem? Todo = null);

/// <summary>Session-only continuation state. It deliberately has no persistence dependency.</summary>
public sealed class TodoContinuationState
{
    private readonly TodoProposalService _proposals;
    private readonly ConcurrentDictionary<string, PendingTodoDraft> _drafts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string DraftId, string TodoId)> _idempotency = new(StringComparer.Ordinal);

    public TodoContinuationState(TodoProposalService proposals) => _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));

    public PendingTodoDraft Replace(string conversationId, string servantId, IReadOnlyList<TodoProposal> proposals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count is < 1 or > 10) throw new ArgumentException("A pending draft must contain 1 to 10 proposals.", nameof(proposals));

        var key = Key(conversationId, servantId);
        var next = _drafts.AddOrUpdate(key,
            _ => new PendingTodoDraft("draft-" + Guid.NewGuid().ToString("N"), conversationId, servantId, 1, proposals.ToArray()),
            (_, current) => current with { Version = checked(current.Version + 1), Proposals = proposals.ToArray(), Status = TodoDraftStatus.Pending, CreatedTodoId = null });
        return next;
    }

    public PendingTodoDraft? Get(string conversationId, string servantId) =>
        _drafts.TryGetValue(Key(conversationId, servantId), out var draft) ? draft : null;

    public void ClearServant(string servantId)
    {
        foreach (var pair in _drafts.Where(pair => pair.Value.ServantId == servantId).ToArray())
        {
            _drafts.TryRemove(pair.Key, out _);
        }
    }

    public TodoDraftResult Cancel(string conversationId, string servantId, string? draftId = null)
    {
        var key = Key(conversationId, servantId);
        if (!_drafts.TryGetValue(key, out var draft)) return new(TodoDraftResultKind.Cancelled);
        if (draftId is not null && !string.Equals(draftId, draft.DraftId, StringComparison.Ordinal)) return new(TodoDraftResultKind.Stale, draft);
        _drafts.TryRemove(key, out _);
        return new(TodoDraftResultKind.Cancelled, draft with { Status = TodoDraftStatus.Cancelled });
    }

    public TodoDraftResult Confirm(string conversationId, string servantId, string draftId, int version, string idempotencyKey)
    {
        var key = Key(conversationId, servantId);
        if (_idempotency.TryGetValue(idempotencyKey, out var prior))
        {
            return new(TodoDraftResultKind.AlreadyCommitted, Get(conversationId, servantId), _proposals.GetCreated(prior.TodoId));
        }

        if (!_drafts.TryGetValue(key, out var draft)) return new(TodoDraftResultKind.Stale);
        if (!string.Equals(draft.DraftId, draftId, StringComparison.Ordinal) || draft.Version != version) return new(TodoDraftResultKind.Stale, draft);
        if (draft.Status == TodoDraftStatus.Committed) return new(TodoDraftResultKind.AlreadyCommitted, draft, draft.CreatedTodoId is null ? null : _proposals.GetCreated(draft.CreatedTodoId));

        var confirming = draft with { Status = TodoDraftStatus.Confirming };
        _drafts[key] = confirming;
        try
        {
            // The canonical flow is one logical parent Todo. The parser still accepts
            // the legacy bounded envelope; each proposal is committed only once.
            var todos = draft.Proposals.Select(_proposals.Confirm).ToArray();
            var todo = todos[0];
            var committed = confirming with { Status = TodoDraftStatus.Committed, CreatedTodoId = todo.Id };
            _drafts[key] = committed;
            _idempotency.TryAdd(idempotencyKey, (draft.DraftId, todo.Id));
            _drafts.TryRemove(key, out _);
            return new(TodoDraftResultKind.Committed, committed, todo);
        }
        catch (Exception)
        {
            _drafts[key] = confirming with { Status = TodoDraftStatus.Unknown };
            return new(TodoDraftResultKind.Unknown, _drafts[key]);
        }
    }

    private static string Key(string conversationId, string servantId) => servantId.Trim() + "/" + conversationId.Trim();
}
