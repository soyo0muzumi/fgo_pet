using System.IO.Pipes;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Privacy;
using FgoPet.AgentProtocol.Validation;

namespace FgoPet.CodexAdapter.Relay;

public interface ICodexRelaySession
{
    Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default);
}

public sealed class CodexRelaySession : ICodexRelaySession
{
    private readonly string _pipeName;
    private readonly string _credential;
    private readonly TimeSpan _connectTimeout;

    public CodexRelaySession(string pipeName, string credential, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _credential = credential;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default)
    {
        var envelope = ProtocolEnvelope.Create(
            $"event-{message.SourceType}-{message.SourceInstance}-{message.TaskId}-{message.Sequence}",
            "agent_event",
            AgentPayloadSanitizer.Sanitize(message));
        AgentProtocolValidator.Validate(envelope);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var reader = new StreamReader(pipe);
        await writer.WriteLineAsync(envelope.ToJson()).ConfigureAwait(false);
        _ = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
    }
}
