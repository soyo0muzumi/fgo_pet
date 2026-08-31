using System.IO;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.Services;

public sealed class AgentDispatchService
{
    private readonly ITodoRepository _todos;
    private readonly IAgentRepository _agents;
    private readonly IAgentGateway _gateway;
    private readonly TimeProvider _time;
    private readonly IAgentRelayAdministration? _administration;
    private readonly AgentEventProjector? _projector;
    private readonly Func<Action, Task> _dispatchToUi;

    public AgentDispatchService(
        ITodoRepository todos,
        IAgentRepository agents,
        IAgentGateway gateway,
        TimeProvider time,
        IAgentRelayAdministration? administration = null,
        AgentEventProjector? projector = null,
        Func<Action, Task>? dispatchToUi = null)
    {
        _todos = todos ?? throw new ArgumentNullException(nameof(todos));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _administration = administration;
        _projector = projector;
        _dispatchToUi = dispatchToUi ?? (action =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public async Task<AgentDispatchResult> DispatchAsync(
        TodoItem todo,
        string sourceType,
        string targetId,
        bool confirmed,
        CancellationToken cancellationToken = default,
        string? sourceInstanceId = null)
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

        if (_administration is not null)
        {
            var snapshot = await _administration.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!snapshot.RelayOnline)
                return new AgentDispatchResult(AgentDispatchStatus.Offline, string.Empty, snapshot.SafeError ?? "relay_offline");
            var candidates = snapshot.Sources.Where(source => source.SourceType == sourceType && source.Enabled
                && source.AllowedTargetIds.Contains(targetId, StringComparer.Ordinal)
                && (sourceInstanceId is null || source.SourceInstanceId == sourceInstanceId)).ToArray();
            if (candidates.Length != 1)
                return new AgentDispatchResult(AgentDispatchStatus.Failed, string.Empty,
                    candidates.Length == 0 ? "target_not_authorized" : "source_instance_required");
            if (!candidates[0].IsOnline)
                return new AgentDispatchResult(AgentDispatchStatus.Offline, string.Empty, "adapter_offline");
            sourceInstanceId = candidates[0].SourceInstanceId;
        }

        var dispatchRequestId = CreateStableRequestId(todo.Id, sourceType, targetId, sourceInstanceId);
        var request = new AgentDispatchRequest(
            dispatchRequestId,
            todo.Id,
            todo.Title,
            todo.Description,
            todo.Priority,
            todo.DueAt,
            sourceType,
            targetId) { SourceInstanceId = sourceInstanceId };
        if (!_gateway.IsConnected)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, dispatchRequestId, "relay_offline");
        }
        var now = _time.GetUtcNow();
        var sourceInstance = sourceInstanceId ?? "relay";
        // Reserve the execution before enqueueing. The Adapter may poll and
        // complete a very fast task before DispatchAsync returns; saving only
        // after the gateway call loses those events and leaves the Todo stuck.
        var reservation = new AgentExecution(
            "execution-" + dispatchRequestId,
            todo.Id,
            sourceType,
            sourceInstance,
            dispatchRequestId,
            dispatchRequestId,
            now);
        _agents.SaveExecution(reservation);
        // Make the local Todo active before enqueueing. A very fast Adapter can
        // emit task_completed while DispatchAsync is still awaiting the relay
        // acknowledgement; activating afterwards with the original planned
        // snapshot would overwrite that terminal state.
        _todos.Save(todo.Activate(now));
        AgentDispatchResult result;
        try
        {
            result = await _gateway.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, dispatchRequestId, "dispatch_outcome_unknown");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, dispatchRequestId, "dispatch_outcome_unknown");
        }

        // An offline response can mean the pipe broke after the Relay accepted
        // the request. Keep the stable reservation for reconciliation instead
        // of treating transport loss as an authoritative rejection.
        if (result.Status == AgentDispatchStatus.Offline
            || result.SafeError is "relay_offline" or "relay_timeout" or "dispatch_outcome_unknown")
        {
            return result with { SafeError = "dispatch_outcome_unknown" };
        }

        if (result.Status is not (AgentDispatchStatus.Accepted or AgentDispatchStatus.AlreadyApplied))
        {
            await FailReservationAsync(reservation, result.SafeError ?? "relay_rejected").ConfigureAwait(false);
            return result;
        }

        var taskId = result.TaskId ?? dispatchRequestId;
        if (!string.Equals(taskId, reservation.TaskId, StringComparison.Ordinal))
        {
            // A successful ack with an unexpected task identity is an unknown
            // remote outcome. Keep the reservation so a retry cannot launch a
            // second task until state for the original request is reconciled.
            return new AgentDispatchResult(AgentDispatchStatus.Failed, dispatchRequestId, "relay_task_id_mismatch");
        }
        return result;
    }

    private async Task FailReservationAsync(AgentExecution reservation, string summary)
    {
        try
        {
            // Transport failure and cancellation do not call this method: the
            // remote outcome is unknown in those cases. For an explicit Relay
            // rejection, only a reservation that is still dispatching can be
            // failed locally. An observed remote event owns its sequence and
            // status and must be left untouched.
            var current = _agents.GetExecution(reservation.SourceType, reservation.SourceInstance, reservation.TaskId);
            if (current is null || current.Status != AgentExecutionStatus.Dispatching)
            {
                return;
            }

            var failure = new AgentEvent(
                reservation.SourceType,
                reservation.SourceInstance,
                reservation.TaskId,
                1,
                AgentEventType.TaskFailed,
                _time.GetUtcNow(),
                summary: summary,
                TodoId: reservation.TodoId,
                DispatchRequestId: reservation.DispatchRequestId);
            if (_projector is null)
            {
                _agents.ApplyEvent(failure);
            }
            else
            {
                await _dispatchToUi(() => _projector.Apply(failure)).ConfigureAwait(false);
            }
        }
        catch (KeyNotFoundException) { }
    }

    private static string CreateStableRequestId(string todoId, string sourceType, string targetId, string? sourceInstanceId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{todoId}\n{sourceType}\n{sourceInstanceId}\n{targetId}"));
        return "dispatch-" + Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
