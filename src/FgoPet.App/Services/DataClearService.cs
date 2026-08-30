using FgoPet.Core.Agents;
using FgoPet.Core.Todo;

namespace FgoPet.App.Services;

/// <summary>Clears only Agent-owned Todo data; connection pairing remains separate.</summary>
public sealed class DataClearService
{
    private readonly ITodoRepository _todos;
    private readonly IAgentGateway? _gateway;

    public DataClearService(ITodoRepository todos, IAgentGateway? gateway = null)
    {
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
        _gateway = gateway;
    }

    public void ClearAgentTodoData()
    {
        _todos.ClearAgentTodoData();
        _gateway?.ClearPendingEventsAsync().GetAwaiter().GetResult();
    }
}
