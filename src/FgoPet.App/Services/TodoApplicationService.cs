using FgoPet.Core.Todo;
using FgoPet.Core.Agents;

namespace FgoPet.App.Services;

/// <summary>Local Todo operations. Agent-owned work remains protected.</summary>
public sealed class TodoApplicationService
{
    private readonly ITodoRepository _repository;
    private readonly TimeProvider _time;
    private readonly IAgentRepository? _agents;
    public event Action? Changed;

    public TodoApplicationService(ITodoRepository repository, TimeProvider time, IAgentRepository? agents = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _agents = agents;
    }

    public TodoItem Create(string title, string? description, TodoPriority priority, DateTimeOffset? dueAt)
    {
        var now = _time.GetUtcNow();
        var todo = new TodoItem(Guid.NewGuid().ToString("N"), title, description, priority, dueAt, now, now);
        Persist(todo);
        return todo;
    }

    public IReadOnlyList<TodoItem> ListActive() => _repository.List()
        .Where(item => item.Status is TodoStatus.Planned or TodoStatus.Active).ToArray();
    public IReadOnlyList<TodoItem> ListHistory() => _repository.List(TodoStatus.Completed)
        .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt).ToArray();
    public IReadOnlyList<TodoItem> ListHistoryOn(DateOnly localDate) => _repository.ListCompletedOn(localDate)
        .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt).ToArray();
    public TodoItem? Get(string id) => _repository.Get(id);
    public void Save(TodoItem todo) => Persist(todo);

    public bool IsProtected(TodoItem todo) => todo.Status == TodoStatus.Active
        || _agents?.GetLatestExecutionForTodo(todo.Id) is { IsTerminal: false };

    public TodoItem Update(string id, string title, string? description)
    {
        var current = RequireEditable(id);
        var updated = new TodoItem(current.Id, title, description, current.Priority, current.DueAt,
            current.CreatedAt, _time.GetUtcNow(), current.Status, current.CompletedAt);
        ApplyLocal(current, updated);
        return updated;
    }

    public TodoItem Complete(string id)
    {
        var current = RequireEditable(id);
        var completed = current.Complete(_time.GetUtcNow());
        ApplyLocal(current, completed);
        return completed;
    }

    public void UndoCompletion(TodoItem completed)
    {
        var current = RequireEditable(completed.Id);
        if (current != completed || current.Status != TodoStatus.Completed)
            throw new InvalidOperationException("任务已发生变化，不能撤销这次完成。");
        // Only the exact local completion snapshot can be undone.
        ApplyLocal(current, current with { Status = TodoStatus.Planned, CompletedAt = null, UpdatedAt = _time.GetUtcNow() });
    }

    public void Delete(string id)
    {
        ApplyLocal(RequireEditable(id), null);
    }

    private TodoItem RequireEditable(string id)
    {
        var todo = _repository.Get(id) ?? throw new KeyNotFoundException("待办已不存在。");
        if (IsProtected(todo))
            throw new InvalidOperationException("任务仍有关联的 Agent 执行，请先在兼容记录中核对结果。");
        return todo;
    }

    private void ApplyLocal(TodoItem expected, TodoItem? replacement)
    {
        if (!_repository.TryUpdateLocal(expected, replacement))
            throw new InvalidOperationException("任务已变化或仍有活动执行，请刷新后核对。");
        NotifyChanged();
    }
    private void NotifyChanged()
    {
        // Persistence is already committed; a stale UI observer must not make callers retry the write.
        foreach (Action observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { observer(); } catch (Exception) { }
        }
    }

    private void Persist(TodoItem todo)
    {
        _repository.Save(todo);
        NotifyChanged();
    }
}
