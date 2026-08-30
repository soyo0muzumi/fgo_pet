using System.IO.Pipes;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.Core.Agents;

namespace FgoPet.Infrastructure.Agents;

public sealed class AgentRelayClient : IAgentGateway
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private volatile bool _connected;
    private DateTimeOffset? _lastEventAtUtc;
    public event Action<AgentEvent>? EventReceived;
    public AgentRelayClient(string pipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? $"fgo-pet-agent-app-{Environment.UserName}-v1"
            : pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
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
            response.RootElement.TryGetProperty("protocol_version", out var version) && version.ValueKind == JsonValueKind.String
                ? version.GetString() ?? ProtocolEnvelope.CurrentProtocolVersion
                : ProtocolEnvelope.CurrentProtocolVersion,
            _lastEventAtUtc,
            response.RootElement.TryGetProperty("pending_count", out var pending) && pending.TryGetInt32(out var count) ? count : 0);
    }

    public async Task<AgentDispatchResult> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = new DispatchTaskRequest(
            request.DispatchRequestId,
            request.TodoId,
            request.Title,
            request.Description,
            request.Priority.ToString().ToLowerInvariant(),
            request.DueAt,
            request.TargetId);
        var envelope = ProtocolEnvelope.Create("dispatch-" + request.DispatchRequestId, "dispatch_task", message);
        AgentProtocolValidator.Validate(envelope);
        var response = await SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return new AgentDispatchResult(AgentDispatchStatus.Offline, request.DispatchRequestId, "relay_offline");
        }

        var result = response.RootElement.GetProperty("result").GetString();
        return new AgentDispatchResult(
            result switch
            {
                "accepted" => AgentDispatchStatus.Accepted,
                "alreadyapplied" => AgentDispatchStatus.AlreadyApplied,
                _ => AgentDispatchStatus.Failed,
            },
            request.DispatchRequestId,
            result is "accepted" or "alreadyapplied" ? null : "relay_rejected",
            ReadOptionalString(response.RootElement, "task_id"),
            ReadOptionalString(response.RootElement, "source_instance"));
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

        var status = response.RootElement.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.String
            && Enum.TryParse<AgentOpenTaskStatus>(result.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : AgentOpenTaskStatus.Unsupported;
        return new AgentOpenTaskResult(status, ReadOptionalString(response.RootElement, "error"));
    }

    public async Task ClearPendingEventsAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(
            ProtocolEnvelope.Create("clear-" + Guid.NewGuid().ToString("N"), "status_check", new { clear_pending = true }),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetConnectionEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await SendAsync(
            ProtocolEnvelope.Create("connection-" + Guid.NewGuid().ToString("N"), "status_check", new { enabled }),
            cancellationToken).ConfigureAwait(false);
    }

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

        var events = ReadEvents(response.RootElement);
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

    private async Task<JsonDocument?> SendAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            using var reader = new StreamReader(pipe);
            await writer.WriteLineAsync(envelope.ToJson()).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            _connected = true;
            _lastEventAtUtc = DateTimeOffset.UtcNow;
            return line is null ? null : JsonDocument.Parse(line);
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
            if (item.ValueKind != JsonValueKind.String) continue;
            var envelope = ProtocolEnvelope.Parse(item.GetString()!);
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
