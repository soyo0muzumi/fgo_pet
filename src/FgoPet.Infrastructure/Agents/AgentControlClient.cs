using System.Text;
using System.Text.Json;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.Infrastructure.Agents;

public sealed class AgentRelayException(string safeError) : IOException(safeError)
{
    public string SafeError { get; } = safeError;
}

/// <summary>Credential-free, bounded App control transport. Shared by polling and settings.</summary>
public sealed class AgentControlClient
{
    private readonly Func<ProtocolEnvelope, CancellationToken, Task<string>> _send;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AgentControlClient(string pipeName, TimeSpan timeout)
        : this(new JsonLinePipeClient(pipeName, timeout).SendAsync) { }

    public AgentControlClient(Func<ProtocolEnvelope, CancellationToken, Task<string>> send) => _send = send;

    public async Task<ProtocolEnvelope> SendAsync(ProtocolEnvelope request, CancellationToken cancellationToken = default)
    {
        AgentProtocolValidator.Validate(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = ProtocolEnvelope.Parse(await _send(request, cancellationToken).ConfigureAwait(false));
            if (response.MessageId != request.MessageId || response.MessageType != request.MessageType && response.MessageType != "error")
                throw new AgentRelayException("invalid_response");
            if (response.ProtocolVersion != ProtocolEnvelope.CurrentProtocolVersion)
                throw new AgentRelayException("version_mismatch");
            AgentProtocolValidator.ValidateResponse(response);
            if (response.MessageType == "error")
                throw new AgentRelayException(response.Payload.GetProperty("result").GetString() == "unauthorized"
                    ? "authentication_failed" : "operation_rejected");
            return response;
        }
        catch (AgentRelayException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AgentRelayException("relay_timeout"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException)
        { throw new AgentRelayException("relay_offline"); }
        catch (Exception error) when (error is AgentProtocolValidationException or JsonException or InvalidDataException or DecoderFallbackException)
        { throw new AgentRelayException("invalid_response"); }
        finally { _gate.Release(); }
    }
}
