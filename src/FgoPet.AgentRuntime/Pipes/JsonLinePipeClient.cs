using System.Buffers;
using System.IO.Pipes;
using System.Text;
using FgoPet.AgentProtocol;

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
        if (responses.Count != 2)
        {
            throw new InvalidOperationException("The authenticated pipe exchange returned an incomplete response.");
        }

        return responses[1];
    }

    private async Task<IReadOnlyList<string>> SendFramesAsync(
        IReadOnlyList<string> requests,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCancellation.CancelAfter(_connectTimeout);
        await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);

        foreach (var request in requests)
        {
            await WriteFrameAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        }

        var responses = new List<string>(requests.Count);
        foreach (var _ in requests)
        {
            responses.Add(await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false));
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

    private async Task<string> ReadFrameAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        var frame = new ArrayBufferWriter<byte>(4096);
        try
        {
            while (true)
            {
                var count = await pipe.ReadAsync(rented.AsMemory(0, rented.Length), timeout.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The pipe closed before a complete JSON-line response was received.");
                }

                var newline = Array.IndexOf(rented, (byte)'\n', 0, count);
                var bytesToCopy = newline >= 0 ? newline : count;
                if (frame.WrittenCount + bytesToCopy > MaxFrameBytes)
                {
                    throw new InvalidDataException("The JSON response exceeds the 1 MiB frame limit.");
                }

                rented.AsSpan(0, bytesToCopy).CopyTo(frame.GetSpan(bytesToCopy));
                frame.Advance(bytesToCopy);
                if (newline >= 0)
                {
                    var text = StrictUtf8.GetString(frame.WrittenSpan);
                    return text.EndsWith('\r') ? text[..^1] : text;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be finite and positive.");
        }
    }
}
