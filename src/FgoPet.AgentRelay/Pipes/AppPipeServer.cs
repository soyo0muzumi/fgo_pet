using System.IO.Pipes;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentRelay.Routing;

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
        var request = envelope.DeserializePayload<DispatchTaskRequest>();
        var receipt = _router.RouteDispatch(_credential, request, DateTimeOffset.UtcNow);
        return Task.FromResult($"{{\"result\":\"{receipt.Result.ToString().ToLowerInvariant()}\",\"dispatch_request_id\":\"{request.DispatchRequestId}\"}}");
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
