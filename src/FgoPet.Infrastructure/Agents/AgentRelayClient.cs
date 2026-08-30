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
    public AgentRelayClient(string pipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? $"fgo-pet-agent-app-{Environment.UserName}-v1"
            : pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
    }

    public bool IsConnected => _connected;

    public Task<AgentGatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentGatewayStatus(_connected, ProtocolEnvelope.CurrentProtocolVersion, _lastEventAtUtc, 0));

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
            result is "accepted" or "alreadyapplied" ? null : "relay_rejected");
    }

    public Task<AgentOpenTaskResult> OpenTaskAsync(AgentOpenTaskRequest request, CancellationToken cancellationToken = default)
    {
        _ = request;
        _ = cancellationToken;
        return Task.FromResult(_connected
            ? new AgentOpenTaskResult(AgentOpenTaskStatus.AppOnly, "exact_navigation_not_supported")
            : new AgentOpenTaskResult(AgentOpenTaskStatus.Offline, "relay_offline"));
    }

    public Task<IReadOnlyList<AgentEvent>> QueryKnownStatesAsync(
        IReadOnlyList<AgentExecution> knownExecutions,
        CancellationToken cancellationToken = default)
    {
        _ = knownExecutions;
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<AgentEvent>>(Array.Empty<AgentEvent>());
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
}
