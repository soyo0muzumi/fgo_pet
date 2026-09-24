using System.Collections.Concurrent;
using FgoPet.Core.Todo;

namespace FgoPet.App.Dialogue;

/// <summary>Session-only continuation state. It deliberately has no persistence dependency.</summary>
public sealed class TodoContinuationState : ITodoDraftWorkflow
{
    private readonly TodoProposalService _proposals;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<(string Servant, string Conversation), PendingTodoDraft> _drafts = new();
    private readonly Dictionary<RetryIdentity, (PendingTodoDraft Draft, IReadOnlyList<string> TodoIds)> _idempotency = new();

    public TodoContinuationState(TodoProposalService proposals) => _proposals = proposals ?? throw new ArgumentNullException(nameof(proposals));

    public PendingTodoDraft Replace(string conversationId, string servantId, IReadOnlyList<TodoProposal> proposals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count is < 1 or > 10) throw new ArgumentException("A pending draft must contain 1 to 10 proposals.", nameof(proposals));

        lock (_gate)
        {
            var key = Key(conversationId, servantId);
            var snapshot = Array.AsReadOnly(proposals.ToArray());
            var next = _drafts.AddOrUpdate(key,
                _ => new PendingTodoDraft("draft-" + Guid.NewGuid().ToString("N"), conversationId, servantId, 1, snapshot),
                (_, current) => current with { Version = checked(current.Version + 1), Proposals = snapshot, Status = TodoDraftStatus.Pending, CreatedTodoId = null });
            return next;
        }
    }

    public PendingTodoDraft? Get(string conversationId, string servantId)
    {
        lock (_gate) return _drafts.TryGetValue(Key(conversationId, servantId), out var draft) ? draft : null;
    }

    public void ClearServant(string servantId)
    {
        lock (_gate)
        {
            foreach (var pair in _drafts.Where(pair => pair.Value.ServantId == servantId).ToArray())
                _drafts.TryRemove(pair.Key, out _);
        }
    }

    public string? GetPromptState(string conversationId, string servantId)
    {
        var draft = Get(conversationId, servantId);
        if (draft is null) return null;
        var text = new System.Text.StringBuilder($"当前待确认 Todo 草稿（第 {draft.Version} 版，共 {draft.Proposals.Count} 项，尚未创建）。修改时保留所有未要求更改的提案与字段。\n");
        for (var index = 0; index < draft.Proposals.Count; index++)
        {
            var proposal = draft.Proposals[index];
            text.AppendLine($"第 {index + 1} 项：标题={proposal.Title}；描述={proposal.Description ?? "无"}；优先级={proposal.Priority}；到期={proposal.DueAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "无"}；步骤={string.Join("；", proposal.StepTitles)}。");
        }
        return text.ToString();
    }

    public TodoDraftResult Cancel(string conversationId, string servantId, string? draftId = null)
    {
        lock (_gate)
        {
            var key = Key(conversationId, servantId);
            if (!_drafts.TryGetValue(key, out var draft)) return new(TodoDraftResultKind.Cancelled);
            if (draftId is not null && !string.Equals(draftId, draft.DraftId, StringComparison.Ordinal)) return new(TodoDraftResultKind.Stale, draft);
            _drafts.TryRemove(key, out _);
            return new(TodoDraftResultKind.Cancelled, draft with { Status = TodoDraftStatus.Cancelled });
        }
    }

    public TodoDraftResult Confirm(string conversationId, string servantId, string draftId, int version, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        lock (_gate) return ConfirmCore(conversationId, servantId, draftId, version, idempotencyKey);
    }

    private TodoDraftResult ConfirmCore(string conversationId, string servantId, string draftId, int version, string idempotencyKey)
    {
        var key = Key(conversationId, servantId);
        var retry = new RetryIdentity(key.Servant, key.Conversation, draftId, version, idempotencyKey);
        if (_idempotency.TryGetValue(retry, out var prior))
        {
            var todos = prior.TodoIds
                .Select(_proposals.GetCreated)
                .Where(todo => todo is not null)
                .Cast<TodoItem>()
                .ToArray();
            return new(TodoDraftResultKind.AlreadyCommitted, prior.Draft, todos.FirstOrDefault(), todos);
        }

        if (!_drafts.TryGetValue(key, out var draft)) return new(TodoDraftResultKind.Stale);
        if (!string.Equals(draft.DraftId, draftId, StringComparison.Ordinal) || draft.Version != version) return new(TodoDraftResultKind.Stale, draft);
        if (draft.Status == TodoDraftStatus.Committed) return new(TodoDraftResultKind.AlreadyCommitted, draft, draft.CreatedTodoId is null ? null : _proposals.GetCreated(draft.CreatedTodoId));

        var confirming = draft with { Status = TodoDraftStatus.Confirming };
        _drafts[key] = confirming;
        try
        {
            // A common goal normally produces one parent Todo, while independent
            // goals may produce several. Commit the full bounded envelope once.
            var todos = draft.Proposals
                .Select((proposal, index) => _proposals.Confirm(proposal, StableTodoId(draft, index)))
                .ToArray();
            var todo = todos[0];
            var committed = confirming with { Status = TodoDraftStatus.Committed, CreatedTodoId = todo.Id };
            _drafts[key] = committed;
            _idempotency.TryAdd(retry, (committed, todos.Select(item => item.Id).ToArray()));
            _drafts.TryRemove(key, out _);
            return new(TodoDraftResultKind.Committed, committed, todo, todos);
        }
        catch (Exception)
        {
            _drafts[key] = confirming with { Status = TodoDraftStatus.Unknown };
            return new(TodoDraftResultKind.Unknown, _drafts[key]);
        }
    }

    private static (string Servant, string Conversation) Key(string conversationId, string servantId) => (servantId.Trim(), conversationId.Trim());

    private sealed record RetryIdentity(string ServantId, string ConversationId, string DraftId, int Version, string RequestId);

    private static string StableTodoId(PendingTodoDraft draft, int index) =>
        $"draft-{draft.DraftId}-{draft.Version}-{index}";
}
