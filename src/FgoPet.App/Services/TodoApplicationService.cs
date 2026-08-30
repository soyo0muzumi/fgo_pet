using FgoPet.Core.Todo;

namespace FgoPet.App.Services;

/// <summary>Application-facing CRUD for user-created Todo items.</summary>
public sealed class TodoApplicationService
{
    private readonly ITodoRepository _repository;
    private readonly TimeProvider _time;

    public TodoApplicationService(ITodoRepository repository, TimeProvider time)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public TodoItem Create(
        string title,
        string? description,
        TodoPriority priority,
        DateTimeOffset? dueAt)
    {
        var now = _time.GetUtcNow();
        var todo = new TodoItem(
            Guid.NewGuid().ToString("N"),
            title,
            description,
            priority,
            dueAt,
            now,
            now);
        _repository.Save(todo);
        return todo;
    }

    public IReadOnlyList<TodoItem> ListActive() => _repository.List()
        .Where(item => item.Status is TodoStatus.Planned or TodoStatus.Active)
        .ToArray();

    public IReadOnlyList<TodoItem> ListHistory() => _repository.List(TodoStatus.Completed)
        .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt)
        .ToArray();

    public IReadOnlyList<TodoItem> ListHistoryOn(DateOnly localDate) => _repository
        .ListCompletedOn(localDate)
        .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt)
        .ToArray();

    public TodoItem? Get(string id) => _repository.Get(id);

    public void Save(TodoItem todo) => _repository.Save(todo);

    public void Delete(string id)
    {
        var todo = _repository.Get(id) ?? throw new KeyNotFoundException($"Todo '{id}' was not found.");
        if (todo.Status == TodoStatus.Active)
        {
            throw new InvalidOperationException("Active Todo items must be cancelled in the Agent before deletion.");
        }

        _repository.Delete(id);
    }
}
