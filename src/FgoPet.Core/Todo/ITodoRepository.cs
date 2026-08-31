namespace FgoPet.Core.Todo;

public interface ITodoRepository
{
    void Save(TodoItem todo);
    TodoItem? Get(string id);
    IReadOnlyList<TodoItem> List(TodoStatus? status = null);
    IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate);
    void Delete(string id);
    void ClearAgentTodoData();
}
