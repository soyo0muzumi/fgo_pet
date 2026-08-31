using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using FgoPet.AgentRuntime.Pipes;

namespace FgoPet.CodexAdapter.Relay;

public interface ICodexRelaySession
{
    Task SendEventAsync(AgentEventMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Adapter-only transport; credentials belong to each authenticated exchange, not this client.</summary>
public sealed class CodexRelaySession : IAdapterRelayTransport
{
    private readonly JsonLinePipeClient _client;

    public CodexRelaySession(string pipeName, TimeSpan? connectTimeout = null) =>
        _client = new JsonLinePipeClient(pipeName, connectTimeout ?? TimeSpan.FromMilliseconds(500));

    public async Task<ProtocolEnvelope> SendAsync(ProtocolEnvelope request, AuthenticateRequest? authentication = null,
        CancellationToken cancellationToken = default)
    {
        AgentProtocolValidator.Validate(request);
        ProtocolEnvelope? auth = authentication is null ? null :
            ProtocolEnvelope.Create("auth-" + Guid.NewGuid().ToString("N"), "authenticate", authentication);
        if (auth is not null) AgentProtocolValidator.Validate(auth);
        var line = auth is null
            ? await _client.SendAsync(request, cancellationToken).ConfigureAwait(false)
            : await _client.SendAuthenticatedAsync(auth, request, cancellationToken).ConfigureAwait(false);
        var response = ProtocolEnvelope.Parse(line);
        if (response.ProtocolVersion != ProtocolEnvelope.CurrentProtocolVersion)
            throw new AdapterConnectionException(new(AdapterConnectionStatus.VersionMismatch));

        AgentProtocolValidator.ValidateResponse(response);
        var authFailure = auth is not null && response.MessageId == auth.MessageId
            && response.MessageType is "authenticate" or "error"
            && response.Payload.TryGetProperty("result", out var status)
            && status.ValueKind == System.Text.Json.JsonValueKind.String && status.GetString() != "authenticated";
        var expectedType = request.MessageType == "registration_request" ? "registration_status" : request.MessageType;
        if (!authFailure && (response.MessageId != request.MessageId
            || response.MessageType != expectedType && response.MessageType != "error"))
            throw new AgentProtocolValidationException("The relay response does not match its request.");
        return response;
    }
}
