namespace FgoPet.Core.Todo;

public interface ITodoRepository
{
    void Save(TodoItem todo);
    bool TryUpdateLocal(TodoItem expected, TodoItem? replacement)
    {
        if (Get(expected.Id) != expected || expected.Status == TodoStatus.Active) return false;
        if (replacement is null) Delete(expected.Id); else Save(replacement);
        return true;
    }
    TodoItem? Get(string id);
    IReadOnlyList<TodoItem> List(TodoStatus? status = null);
    IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate);
    void Delete(string id);
    void ClearAgentTodoData();
}
