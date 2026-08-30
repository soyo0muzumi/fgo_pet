using FgoPet.Core.Todo;

namespace FgoPet.App.Services;

/// <summary>Clears only Agent-owned Todo data; connection pairing remains separate.</summary>
public sealed class DataClearService
{
    private readonly ITodoRepository _todos;

    public DataClearService(ITodoRepository todos) => _todos = todos ?? throw new ArgumentNullException(nameof(todos));

    public void ClearAgentTodoData() => _todos.ClearAgentTodoData();
}
