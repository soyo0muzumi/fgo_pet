using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.Core.Agents;
using FgoPet.AgentRuntime;

namespace FgoPet.Infrastructure.Agents;

public sealed class AgentRelayClient : IAgentGateway
{
    private readonly AgentControlClient _control;
    private volatile bool _connected;
    private DateTimeOffset? _lastEventAtUtc;
    public event Action<AgentEvent>? EventReceived;
    public AgentRelayClient(string pipeName, TimeSpan? connectTimeout = null)
        : this(new AgentControlClient(string.IsNullOrWhiteSpace(pipeName)
            ? RelayPipeNames.ForCurrentUser(RelayRuntimeOptions.ForCurrentUser()).App : pipeName,
            connectTimeout ?? TimeSpan.FromMilliseconds(500))) { }

    public AgentRelayClient(AgentControlClient control)
    {
        _control = control;
    }

    public bool IsConnected => _connected;

    public async Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            ProtocolEnvelope.Create("status-" + Guid.NewGuid().ToString("N"), "status_check", new { include_events = false }),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return new AgentGatewayStatus(false, ProtocolEnvelope.CurrentProtocolVersion, _lastEventAtUtc, 0);
        }

        return new AgentGatewayStatus(
            true,
            response.Payload.TryGetProperty("protocol_version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString() ?? ProtocolEnvelope.CurrentProtocolVersion
                : ProtocolEnvelope.CurrentProtocolVersion,
            _lastEventAtUtc,
            response.Payload.TryGetProperty("pending_count", out var pending) && pending.TryGetInt32(out var count) ? count : 0);
    }

    public async Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceInstanceId))
            return new AgentDispatchResult(AgentDispatchStatus.Failed, request.DispatchRequestId, "source_instance_required");
        var message = new DispatchTaskRequest(
            request.DispatchRequestId,
            request.TodoId,
            request.Title,
            request.Description,
            request.Priority.ToString().ToLowerInvariant(),
            request.DueAt,
            request.TargetId) { SourceType = request.SourceType, SourceInstanceId = request.SourceInstanceId };
        var envelope = ProtocolEnvelope.Create("dispatch-" + request.DispatchRequestId, "dispatch_task", message);
        AgentProtocolValidator.Validate(envelope);
        var response = await SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, request.DispatchRequestId, "relay_offline");
        }

        var result = response.Payload.GetProperty("result").GetString();
        return new AgentDispatchResult(
            result switch
            {
                "accepted" => AgentDispatchStatus.Accepted,
                "alreadyapplied" or "already_applied" => AgentDispatchStatus.AlreadyApplied,
                "offline" => AgentDispatchStatus.Offline,
                _ => AgentDispatchStatus.Failed,
            },
            request.DispatchRequestId,
            result is "accepted" or "alreadyapplied" or "already_applied" ? null : "relay_rejected",
            ReadOptionalString(response.Payload, "task_id"),
            ReadOptionalString(response.Payload, "source_instance"));
    }

    public async Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = ProtocolEnvelope.Create(
            "open-" + request.SourceType + "-" + request.SourceInstance + "-" + request.TaskId,
            "open_task",
            new OpenTaskRequest(request.SourceType, request.SourceInstance, request.TaskId));
        AgentProtocolValidator.Validate(envelope);
        var response = await SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return new AgentOpenTaskResult(AgentOpenTaskStatus.Offline, "relay_offline");
        }

        var status = response.Payload.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.String
            && Enum.TryParse<AgentOpenTaskStatus>(result.GetString()?.Replace("_", ""), ignoreCase: true, out var parsed)
            ? parsed
            : AgentOpenTaskStatus.Unsupported;
        return new AgentOpenTaskResult(status, ReadOptionalString(response.Payload, "error"));
    }

    public async Task ClearPendingEventsAsync(CancellationToken cancellationToken = default)
    {
        await _control.SendAsync(
            ProtocolEnvelope.Create("clear-" + Guid.NewGuid().ToString("N"), "status_check", new { clear_pending = true }),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetConnectionEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _control.SendAsync(
            ProtocolEnvelope.Create("connection-" + Guid.NewGuid().ToString("N"), "status_check", new { enabled }),
            cancellationToken).ConfigureAwait(false);
    }

    public Task SetSourceEnabledAsync(string sourceType, bool enabled, CancellationToken cancellationToken = default) =>
        throw new AgentRelayException("source_instance_required");

    public Task SetAllowedTargetsAsync(string sourceType, IReadOnlyList<string> targetIds, CancellationToken cancellationToken = default) =>
        throw new AgentRelayException("source_instance_required");

    public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(
        IReadOnlyList<AgentExecution> knownExecutions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knownExecutions);
        return QueryEventsAsync(knownExecutions, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentEvent>> PollPendingEventsAsync(CancellationToken cancellationToken = default)
    {
        return await QueryEventsAsync(knownExecutions: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<AgentEvent>> QueryEventsAsync(
        IReadOnlyList<AgentExecution>? knownExecutions,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            ProtocolEnvelope.Create("status-" + Guid.NewGuid().ToString("N"), "status_check", new { include_events = true }),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return Array.Empty<AgentEvent>();
        }

        var events = ReadEvents(response.Payload);
        if (events.Count > 0) _lastEventAtUtc = DateTimeOffset.UtcNow;
        if (knownExecutions is null)
        {
            foreach (var agentEvent in events) EventReceived?.Invoke(agentEvent);
            return events;
        }

        var known = knownExecutions
            .Select(execution => $"{execution.SourceType}/{execution.SourceInstance}/{execution.TaskId}")
            .ToHashSet(StringComparer.Ordinal);
        return events.Where(agentEvent => known.Contains(agentEvent.TaskIdentity)).ToArray();
    }

    private async Task<ProtocolEnvelope?> SendAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _control.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            _connected = true;
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _connected = false;
            return null;
        }
        catch (IOException)
        {
            _connected = false;
            return null;
        }
    }

    private static IReadOnlyList<AgentEvent> ReadEvents(JsonElement root)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentEvent>();
        }

        var result = new List<AgentEvent>();
        foreach (var item in events.EnumerateArray())
        {
            var envelope = ProtocolEnvelope.Parse(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
            AgentProtocolValidator.Validate(envelope);
            var message = envelope.DeserializePayload<AgentEventMessage>();
            if (!TryParseEventType(message.EventType, out var eventType)) continue;
            result.Add(new AgentEvent(
                message.SourceType,
                message.SourceInstance,
                message.TaskId,
                message.Sequence,
                eventType,
                message.OccurredAt,
                message.Title,
                message.Summary,
                message.IsPrivate,
                message.TodoId,
                message.DispatchRequestId,
                message.CoveredTaskKeys));
        }

        return result;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryParseEventType(string value, out AgentEventType eventType)
    {
        eventType = value switch
        {
            "task_discovered" => AgentEventType.TaskDiscovered,
            "task_started" => AgentEventType.TaskStarted,
            "task_updated" => AgentEventType.TaskUpdated,
            "attention_required" => AgentEventType.AttentionRequired,
            "task_resumed" => AgentEventType.TaskResumed,
            "milestone_reached" => AgentEventType.MilestoneReached,
            "task_completed" => AgentEventType.TaskCompleted,
            "task_failed" => AgentEventType.TaskFailed,
            "task_cancelled" => AgentEventType.TaskCancelled,
            "task_removed" => AgentEventType.TaskRemoved,
            "goal_completed" => AgentEventType.GoalCompleted,
            _ => default,
        };
        return AgentEventWireNames.IsKnown(value);
    }
}
