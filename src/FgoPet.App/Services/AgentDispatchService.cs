using System.IO;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;

namespace FgoPet.App.Services;

public sealed class AgentDispatchService
{
    private readonly ITodoRepository _todos;
    private readonly IAgentRepository _agents;
    private readonly IAgentGateway _gateway;
    private readonly TimeProvider _time;

    public AgentDispatchService(
        ITodoRepository todos,
        IAgentRepository agents,
        IAgentGateway gateway,
        TimeProvider time)
    {
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<AgentDispatchResult> DispatchAsync(
        TodoItem todo,
        string sourceType,
        string targetId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(todo);
        if (!confirmed)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Failed, string.Empty, "confirmation_required");
        }

        if (!todo.CanDispatch)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Failed, string.Empty, "todo_not_dispatchable");
        }

        var dispatchRequestId = CreateStableRequestId(todo.Id, sourceType, targetId);
        var request = new AgentDispatchRequest(
            dispatchRequestId,
            todo.Id,
            todo.Title,
            todo.Description,
            todo.Priority,
            todo.DueAt,
            sourceType,
            targetId);
        AgentDispatchResult result;
        try
        {
            result = await _gateway.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, dispatchRequestId, "relay_timeout");
        }
        catch (IOException)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, dispatchRequestId, "relay_offline");
        }

        if (result.Status is not (AgentDispatchStatus.Accepted or AgentDispatchStatus.AlreadyApplied))
        {
            return result;
        }

        var now = _time.GetUtcNow();
        var taskId = result.TaskId ?? dispatchRequestId;
        var sourceInstance = result.SourceInstance ?? "relay";
        _agents.SaveExecution(new AgentExecution(
            "execution-" + dispatchRequestId,
            todo.Id,
            sourceType,
            sourceInstance,
            taskId,
            dispatchRequestId,
            now));
        _todos.Save(todo.Activate(now));
        return result;
    }

    private static string CreateStableRequestId(string todoId, string sourceType, string targetId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{todoId}\n{sourceType}\n{targetId}"));
        return "dispatch-" + Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
