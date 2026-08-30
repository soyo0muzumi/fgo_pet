using System.IO.Pipes;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay.Routing;
using System.Text.Json;

namespace FgoPet.AgentRelay.Pipes;

public sealed class AppPipeServer
{
    private readonly RelayRouter _router;
    private readonly string _pipeName;
    private readonly string _credential;

    public AppPipeServer(RelayRouter router, string pipeName, string credential)
    {
        _router = router;
        _pipeName = pipeName;
        _credential = credential;
    }

    public Task<string> ProcessLineAsync(string line)
    {
        var envelope = ProtocolEnvelope.Parse(line);
        AgentProtocolValidator.Validate(envelope);
        if (string.Equals(envelope.MessageType, "status_check", StringComparison.Ordinal))
        {
            _router.SetAppOnline(true);
            if (envelope.Payload.TryGetProperty("clear_pending", out var clear)
                && clear.ValueKind == JsonValueKind.True)
            {
                _router.ClearPending();
            }

            if (envelope.Payload.TryGetProperty("enabled", out var enabled)
                && (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
            {
                _router.SetConnectionEnabled(enabled.GetBoolean());
            }

            var includeEvents = envelope.Payload.TryGetProperty("include_events", out var value)
                && value.ValueKind == JsonValueKind.True;
            var pendingCount = _router.PendingInboundCount;
            var events = includeEvents
                ? _router.DrainInbound().Select(item => item.ToJson()).ToArray()
                : Array.Empty<string>();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                result = "status",
                connected = true,
                protocol_version = ProtocolEnvelope.CurrentProtocolVersion,
                pending_count = pendingCount,
                events,
            }));
        }

        if (string.Equals(envelope.MessageType, "open_task", StringComparison.Ordinal))
        {
            var open = _router.RouteOpen(_credential, envelope.DeserializePayload<OpenTaskRequest>(), DateTimeOffset.UtcNow);
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                result = open.Status.ToString().ToLowerInvariant(),
                error = open.Error,
            }));
        }

        if (!string.Equals(envelope.MessageType, "dispatch_task", StringComparison.Ordinal))
        {
            throw new AgentProtocolValidationException("App pipe accepts dispatch_task messages only.");
        }

        var request = envelope.DeserializePayload<DispatchTaskRequest>();
        var receipt = _router.RouteDispatch(_credential, request, DateTimeOffset.UtcNow);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            result = receipt.Result.ToString().ToLowerInvariant(),
            dispatch_request_id = request.DispatchRequestId,
            task_id = receipt.TaskId,
            source_instance = receipt.SourceInstance,
        }));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                await writer.WriteLineAsync(await ProcessLineAsync(line).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
    }
}
