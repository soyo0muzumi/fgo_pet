using System.IO.Pipes;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRelay.Routing;
using System.Text.Json;

namespace FgoPet.AgentRelay.Pipes;

public sealed class AdapterPipeServer
{
    private readonly RelayRouter _router;
    private readonly string _pipeName;
    private readonly string _credential;

    public AdapterPipeServer(RelayRouter router, string pipeName, string credential)
    {
        _router = router;
        _pipeName = pipeName;
        _credential = credential;
    }

    public Task<string> ProcessLineAsync(string line)
    {
        var envelope = ProtocolEnvelope.Parse(line);
        if (string.Equals(envelope.MessageType, "status_check", StringComparison.Ordinal))
        {
            AgentProtocolValidator.Validate(envelope);
            var includeDispatches = envelope.Payload.TryGetProperty("include_dispatches", out var value)
                && value.ValueKind == JsonValueKind.True;
            var dispatches = includeDispatches
                ? _router.DrainOutbound(_credential, DateTimeOffset.UtcNow)
                    .Select(item => ProtocolEnvelope.Create(
                        "dispatch-" + item.Request.DispatchRequestId,
                        "dispatch_task",
                        item.Request,
                        item.EnqueuedAt)
                        .ToJson())
                    .ToArray()
                : Array.Empty<string>();
            return Task.FromResult(JsonSerializer.Serialize(new { result = "dispatches", dispatches }));
        }

        var receipt = _router.RouteAdapterEvent(_credential, envelope, DateTimeOffset.UtcNow);
        return Task.FromResult($"{{\"result\":\"{receipt.Result.ToString().ToLowerInvariant()}\"}}");
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
                var response = await ProcessLineAsync(line).ConfigureAwait(false);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
    }
}
