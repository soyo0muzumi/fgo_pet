using System.IO.Pipes;
using System.Text;
using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Validation;

namespace FgoPet.AgentRuntime.Pipes;

/// <summary>Bounded JSON-line transport for one or two requests on a named pipe.</summary>
public sealed class JsonLinePipeClient
{
    public const int MaxFrameBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _writeTimeout;
    private readonly TimeSpan _readTimeout;

    public JsonLinePipeClient(
        string pipeName,
        TimeSpan connectTimeout,
        TimeSpan? writeTimeout = null,
        TimeSpan? readTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ValidateTimeout(connectTimeout, nameof(connectTimeout));
        _pipeName = pipeName;
        _connectTimeout = connectTimeout;
        _writeTimeout = writeTimeout ?? connectTimeout;
        _readTimeout = readTimeout ?? connectTimeout;
        ValidateTimeout(_writeTimeout, nameof(writeTimeout));
        ValidateTimeout(_readTimeout, nameof(readTimeout));
    }

    public Task<string> SendAsync(ProtocolEnvelope request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(request.ToJson(), cancellationToken);
    }

    public async Task<string> SendAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestJson);
        return await SendFramesAsync([requestJson], cancellationToken).ConfigureAwait(false) is [var response]
            ? response
            : throw new InvalidOperationException("The pipe returned no response.");
    }

    public Task<string> SendAuthenticatedAsync(
        ProtocolEnvelope authenticateRequest,
        ProtocolEnvelope operationRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticateRequest);
        ArgumentNullException.ThrowIfNull(operationRequest);
        return SendAuthenticatedAsync(authenticateRequest.ToJson(), operationRequest.ToJson(), cancellationToken);
    }

    public async Task<string> SendAuthenticatedAsync(
        string authenticateRequestJson,
        string operationRequestJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticateRequestJson);
        ArgumentNullException.ThrowIfNull(operationRequestJson);
        var responses = await SendFramesAsync([authenticateRequestJson, operationRequestJson], cancellationToken)
            .ConfigureAwait(false);
        // A rejected authentication is returned intact, without issuing the operation.
        return responses[^1];
    }

    private async Task<IReadOnlyList<string>> SendFramesAsync(
        IReadOnlyList<string> requests,
        CancellationToken cancellationToken)
    {
        var authenticationRequest = requests.Count == 2 ? ProtocolEnvelope.Parse(requests[0]) : null;
        if (authenticationRequest is not null)
        {
            AgentProtocolValidator.Validate(authenticationRequest);
            if (authenticationRequest.MessageType != "authenticate")
                throw new AgentProtocolValidationException("The first request must authenticate the connection.");
        }
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(_connectTimeout);
        await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);

        var responses = new List<string>(requests.Count);
        var reader = new JsonLineFrameReader(pipe);
        foreach (var request in requests)
        {
            await WriteFrameAsync(pipe, request, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_readTimeout);
            var response = await reader.ReadAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The pipe closed before its response was received.");
            responses.Add(response);
            if (requests.Count == 2 && responses.Count == 1)
            {
                var authentication = ProtocolEnvelope.Parse(response);
                if (authentication.MessageId != authenticationRequest!.MessageId
                    || authentication.MessageType is not "authenticate" and not "error")
                    throw new AgentProtocolValidationException("The authentication response does not match its request.");
                // Let the typed consumer report incompatibility, without sending an operation.
                if (authentication.ProtocolVersion != ProtocolEnvelope.CurrentProtocolVersion) break;
                AgentProtocolValidator.ValidateResponse(authentication);
                if (authentication.MessageType == "error" || authentication.Payload.GetProperty("result").GetString() != "authenticated") break;
            }
        }

        return responses;
    }

    private async Task WriteFrameAsync(Stream pipe, string request, CancellationToken cancellationToken)
    {
        var bytes = StrictUtf8.GetBytes(request);
        if (bytes.Length > MaxFrameBytes)
        {
            throw new InvalidDataException("The JSON request exceeds the 1 MiB frame limit.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_writeTimeout);
        await pipe.WriteAsync(bytes.AsMemory(), timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and positive.");
        }
    }
}
