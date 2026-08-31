using System.Text;
using FgoPet.AgentProtocol;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.AgentRelay.Pipes;

internal sealed class PipeResponseWriter(Stream stream, TimeSpan operationTimeout)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public Task WriteAsync(ProtocolEnvelope response, CancellationToken cancellationToken) =>
        WriteAsync(response.ToJson(), cancellationToken);

    public async Task WriteAsync(string response, CancellationToken cancellationToken)
    {
        if (Utf8.GetByteCount(response) > JsonLinePipeClient.MaxFrameBytes)
            throw new InvalidDataException("response_too_large");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operationTimeout);
        await stream.WriteAsync(Utf8.GetBytes(response + "\n"), deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }
}
