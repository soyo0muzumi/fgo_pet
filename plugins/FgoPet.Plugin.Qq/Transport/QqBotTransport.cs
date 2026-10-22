using System.Net.WebSockets;
using System.Text.Json;
using FgoPet.Core.Secrets;

namespace FgoPet.Plugin.Qq.Transport;

/// <summary>One bot connection. Owns HTTP/WS resources, never a model, conversation, binding or UI dispatcher.</summary>
public sealed class QqBotTransport : IQqBotTransport
{
    private static readonly int[] ReconnectSeconds = [1, 2, 5, 10, 30, 60];
    private readonly object _lifecycle = new();
    private readonly object _messagesGate = new();
    private readonly ICredentialReader _credentials;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly Func<Uri, HttpMessageInvoker, CancellationToken, Task<WebSocket>> _connect;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _replyGate = new(1, 1);
    private readonly Dictionary<string, ReceivedMessage> _received = new(StringComparer.Ordinal);
    private readonly Queue<string> _receivedOrder = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private Task? _disposeTask;
    private QqBotApiClient? _api;
    private bool _disposed;
    private int _running;

    public QqBotTransport(ICredentialReader credentialReader, TimeProvider? timeProvider = null)
        : this(credentialReader, new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15)
        }, ConnectSocketAsync, timeProvider ?? TimeProvider.System) { }

    internal QqBotTransport(ICredentialReader credentialReader, HttpMessageHandler handler,
        Func<Uri, HttpMessageInvoker, CancellationToken, Task<WebSocket>> connect, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credentialReader);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _credentials = credentialReader;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _connect = connect;
        _time = timeProvider;
    }

    public Task RunAsync(QqBotConnection connection, Func<QqC2cMessage, CancellationToken, Task> onMessage,
        Action<QqTransportStatus> onStatus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ArgumentNullException.ThrowIfNull(onStatus);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is not null) throw new InvalidOperationException("QQ_TRANSPORT_ALREADY_STARTED");
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            _runTask = Task.Run(() => RunCoreAsync(connection, onMessage, onStatus, _runCancellation.Token));
            return _runTask;
        }
    }

    private async Task RunCoreAsync(QqBotConnection connection, Func<QqC2cMessage, CancellationToken, Task> onMessage,
        Action<QqTransportStatus> onStatus, CancellationToken token)
    {
        var failed = false;
        var retries = 0;
        var authenticationFailures = 0;
        var session = new GatewaySession();
        try
        {
            if (!QqGatewayProtocol.IsConnectionValid(connection))
                throw new QqTransportException("QQ_CONFIGURATION_INVALID", terminal: true);
            _api = new QqBotApiClient(_http, _credentials, connection, _time);
            Volatile.Write(ref _running, 1);
            while (!token.IsCancellationRequested)
            {
                Notify(onStatus, new(retries == 0 ? QqTransportState.Connecting : QqTransportState.Reconnecting));
                try
                {
                    var accessToken = await _api.GetAccessTokenAsync(token).ConfigureAwait(false);
                    var gateway = await _api.GetGatewayAsync(accessToken, token).ConfigureAwait(false);
                    await RunConnectionAsync(gateway, accessToken, session, onMessage, () =>
                    {
                        retries = 0;
                        authenticationFailures = 0;
                        Notify(onStatus, new(QqTransportState.Connected));
                    }, onStatus, token).ConfigureAwait(false);
                }
                catch (QqTransportException error)
                {
                    if (error.Terminal || (error.Code == "QQ_AUTHENTICATION_FAILED" && ++authenticationFailures >= 3)) throw;
                    if (error.ClearSession) session.Reset();
                    if (error.RefreshToken) _api.InvalidateToken();
                    Notify(onStatus, new(QqTransportState.Reconnecting, error.Code));
                    var delay = error.RetryDelay ?? TimeSpan.FromSeconds(ReconnectSeconds[Math.Min(retries, ReconnectSeconds.Length - 1)]);
                    retries = Math.Min(retries + 1, ReconnectSeconds.Length);
                    await Task.Delay(delay, _time, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (QqTransportException error)
        {
            failed = true;
            Notify(onStatus, new(QqTransportState.Faulted, error.Code));
        }
        catch (Exception)
        {
            failed = true;
            Notify(onStatus, new(QqTransportState.Faulted, "QQ_TRANSPORT_FAILED"));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            _runCancellation?.Cancel();
            _api?.InvalidateToken();
            if (!failed) Notify(onStatus, new(QqTransportState.Stopped));
        }
    }

    private async Task RunConnectionAsync(Uri gateway, string accessToken, GatewaySession session,
        Func<QqC2cMessage, CancellationToken, Task> onMessage, Action onConnected,
        Action<QqTransportStatus> onStatus, CancellationToken token)
    {
        try
        {
            using var connectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
            using var connectToken = CancellationTokenSource.CreateLinkedTokenSource(token, connectDeadline.Token);
            using var socket = await _connect(gateway, _http, connectToken.Token).ConfigureAwait(false);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var sendGate = new SemaphoreSlim(1, 1);
            using var authenticationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
            using var authenticationToken = CancellationTokenSource.CreateLinkedTokenSource(connection.Token, authenticationDeadline.Token);
            QqGatewayFrame hello;
            try { hello = await ReadFrameAsync(socket, authenticationToken.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new QqTransportException("QQ_GATEWAY_HELLO_TIMEOUT"); }
            if (hello.Op != 10 || hello.Data.ValueKind != JsonValueKind.Object
                || !hello.Data.TryGetProperty("heartbeat_interval", out var intervalValue)
                || intervalValue.ValueKind != JsonValueKind.Number || !intervalValue.TryGetInt32(out var milliseconds)
                || milliseconds is < 1000 or > 120_000) throw new QqTransportException("QQ_GATEWAY_HELLO_INVALID");
            object identify = session.SessionId is not null && session.Sequence >= 0
                ? new { op = 6, d = (object)new { token = $"QQBot {accessToken}", session_id = session.SessionId, seq = session.Sequence } }
                : new { op = 2, d = (object)new { token = $"QQBot {accessToken}", intents = QqGatewayProtocol.C2cIntent, shard = new[] { 0, 1 } } };
            await SendFrameAsync(socket, sendGate, identify, connection.Token).ConfigureAwait(false);
            var heartbeat = new HeartbeatState();
            var receive = ReceiveLoopAsync(socket, sendGate, session, heartbeat, authenticationDeadline,
                authenticationToken.Token, connection.Token, onMessage, onConnected, onStatus);
            var pulse = HeartbeatLoopAsync(socket, sendGate, session, heartbeat, TimeSpan.FromMilliseconds(milliseconds), connection.Token);
            var finished = await Task.WhenAny(receive, pulse).ConfigureAwait(false);
            connection.Cancel();
            socket.Abort();
            try { await finished.ConfigureAwait(false); }
            finally
            {
                // Both loops are observed before their socket/gate is disposed; stale callbacks cannot outlive the attempt.
                try { await Task.WhenAll(receive, pulse).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new QqTransportException("QQ_GATEWAY_TIMEOUT"); }
        catch (WebSocketException) { throw new QqTransportException("QQ_GATEWAY_NETWORK_FAILED"); }
        catch (HttpRequestException) { throw new QqTransportException("QQ_GATEWAY_NETWORK_FAILED"); }
        catch (IOException) { throw new QqTransportException("QQ_GATEWAY_NETWORK_FAILED"); }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, SemaphoreSlim sendGate, GatewaySession session,
        HeartbeatState heartbeat, CancellationTokenSource authenticationDeadline, CancellationToken authenticationToken,
        CancellationToken token, Func<QqC2cMessage, CancellationToken, Task> onMessage, Action onConnected,
        Action<QqTransportStatus> onStatus)
    {
        var ready = false;
        while (true)
        {
            QqGatewayFrame frame;
            try { frame = await ReadFrameAsync(socket, ready ? token : authenticationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new QqTransportException("QQ_GATEWAY_AUTH_TIMEOUT"); }
            switch (frame.Op)
            {
                case 0:
                    if (frame.EventType == "READY")
                    {
                        if (!QqGatewayProtocol.TryString(frame.Data, "session_id", 256, out var id) || id.Any(char.IsControl))
                            throw new QqTransportException("QQ_GATEWAY_SESSION_INVALID", clearSession: true);
                        session.SessionId = id;
                        ready = true;
                        authenticationDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                        onConnected();
                    }
                    else if (frame.EventType == "RESUMED")
                    {
                        if (session.SessionId is null) throw new QqTransportException("QQ_GATEWAY_SESSION_INVALID", clearSession: true);
                        ready = true;
                        authenticationDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                        onConnected();
                    }
                    else if ((QqGatewayProtocol.ReadTextMessage(frame) ?? QqGatewayProtocol.ReadUnsupportedMediaMessage(frame)) is { } message)
                    {
                        // During RESUME the gateway replays messages BEFORE RESUMED. A retained session admits those.
                        if (!ready && session.SessionId is null) throw new QqTransportException("QQ_GATEWAY_NOT_AUTHENTICATED");
                        var now = _time.GetUtcNow();
                        if (message.Timestamp > now + TimeSpan.FromMinutes(1) || now - message.Timestamp >= QqGatewayProtocol.ReplyWindow)
                        {
                            Notify(onStatus, new(QqTransportState.Connected, "QQ_MESSAGE_EXPIRED"));
                        }
                        else if (AdmitMessage(message))
                        {
                            try { await onMessage(message, token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception) { Notify(onStatus, new(QqTransportState.Connected, "QQ_MESSAGE_HANDLER_FAILED")); }
                        }
                    }
                    break;
                case 1:
                    heartbeat.MarkPending(_time);
                    await SendFrameAsync(socket, sendGate, HeartbeatPayload(session), token).ConfigureAwait(false);
                    break;
                case 7:
                    throw new QqTransportException("QQ_GATEWAY_RECONNECT");
                case 9:
                    var resumable = frame.Data.ValueKind == JsonValueKind.True;
                    throw new QqTransportException("QQ_GATEWAY_SESSION_INVALID", clearSession: !resumable,
                        refreshToken: !resumable, retryDelay: TimeSpan.FromSeconds(3));
                case 10:
                    throw new QqTransportException("QQ_GATEWAY_HELLO_INVALID");
                case 11:
                    heartbeat.Acknowledge();
                    break;
            }
            if (frame.Sequence is { } sequence) session.Sequence = sequence;
        }
    }

    private async Task HeartbeatLoopAsync(WebSocket socket, SemaphoreSlim sendGate, GatewaySession session,
        HeartbeatState heartbeat, TimeSpan interval, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(interval, _time, token).ConfigureAwait(false);
            heartbeat.MarkPending(_time, interval);
            await SendFrameAsync(socket, sendGate, HeartbeatPayload(session), token).ConfigureAwait(false);
        }
    }

    private static object HeartbeatPayload(GatewaySession session) => new { op = 1, d = session.Sequence >= 0 ? (long?)session.Sequence : null };

    private async Task SendFrameAsync(WebSocket socket, SemaphoreSlim sendGate, object payload, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        await sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
        }
        finally { sendGate.Release(); }
    }

    internal static async Task<QqGatewayFrame> ReadFrameAsync(WebSocket socket, CancellationToken token)
    {
        using var frame = new MemoryStream();
        var buffer = new byte[8192];
        for (var fragments = 0; fragments < 128; fragments++)
        {
            var part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) throw Closed((int?)part.CloseStatus ?? 1006);
            if (part.MessageType != WebSocketMessageType.Text) throw new QqTransportException("QQ_FRAME_INVALID");
            if (frame.Length + part.Count > QqGatewayProtocol.MaxFrameBytes) throw new QqTransportException("QQ_FRAME_TOO_LARGE");
            frame.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return QqGatewayProtocol.Parse(frame.ToArray());
        }
        throw new QqTransportException("QQ_FRAME_TOO_FRAGMENTED");
    }

    private static QqTransportException Closed(int code) => code switch
    {
        4914 or 4915 => new("QQ_GATEWAY_ACCESS_DENIED", terminal: true),
        4001 or 4002 or >= 4010 and <= 4014 => new("QQ_GATEWAY_CONFIGURATION_INVALID", terminal: true),
        4004 => new("QQ_AUTHENTICATION_FAILED", refreshToken: true),
        4006 or 4007 => new("QQ_GATEWAY_SESSION_INVALID", clearSession: true, refreshToken: true),
        // QQ expires an otherwise resumable connection. Keep the last processed session/sequence.
        4009 => new("QQ_GATEWAY_CONNECTION_EXPIRED"),
        4008 => new("QQ_RATE_LIMITED", retryDelay: TimeSpan.FromMinutes(1)),
        >= 4900 and <= 4913 => new("QQ_GATEWAY_UNAVAILABLE", clearSession: true, refreshToken: true),
        _ => new("QQ_GATEWAY_DISCONNECTED")
    };

    private static async Task<WebSocket> ConnectSocketAsync(Uri gateway, HttpMessageInvoker invoker, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("User-Agent", "FgoPet-Qq/1.0");
            // Sharing the owned no-redirect HTTP invoker also prohibits redirects during the WS upgrade.
            await socket.ConnectAsync(gateway, invoker, token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool AdmitMessage(QqC2cMessage message)
    {
        lock (_messagesGate)
        {
            if (_received.ContainsKey(message.MessageId)) return false;
            while (_received.Count >= 4096) _received.Remove(_receivedOrder.Dequeue());
            _received.Add(message.MessageId, new(message.UserOpenId, message.Timestamp));
            _receivedOrder.Enqueue(message.MessageId);
            return true;
        }
    }

    public async Task<QqSendResult> ReplyTextAsync(QqC2cMessage message, string text, CancellationToken cancellationToken)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.MessageId) || message.MessageId.Length > 512
            || !QqGatewayProtocol.IsOpenId(message.UserOpenId) || string.IsNullOrWhiteSpace(text) || text.Length > QqGatewayProtocol.MaxTextLength
            || text.Contains('\0') || !QqGatewayProtocol.IsValidUtf16(text)) return new(false, "QQ_REPLY_INVALID");
        CancellationToken stopping;
        lock (_lifecycle)
        {
            if (_disposed || Volatile.Read(ref _running) == 0 || _api is null || _runCancellation is null)
                return new(false, "QQ_TRANSPORT_STOPPED");
            stopping = _runCancellation.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        await _replyGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ReceivedMessage? admitted;
            lock (_messagesGate) _received.TryGetValue(message.MessageId, out admitted);
            if (admitted is null || admitted.UserOpenId != message.UserOpenId || admitted.Timestamp != message.Timestamp)
                return new(false, "QQ_REPLY_NOT_ADMITTED");
            var chunks = SplitText(text);
            if (admitted.NextSequence + chunks.Count - 1 > 4) return new(false, "QQ_REPLY_LIMIT");
            var sent = 0;
            foreach (var chunk in chunks)
            {
                if (_time.GetUtcNow() - admitted.Timestamp >= QqGatewayProtocol.ReplyWindow)
                    return new(false, sent > 0 ? "QQ_SEND_PARTIAL" : "QQ_REPLY_EXPIRED");
                var sequence = admitted.NextSequence++;
                var result = await _api!.ReplyAsync(admitted.UserOpenId, message.MessageId, chunk, sequence, linked.Token).ConfigureAwait(false);
                if (!result.Succeeded) return sent > 0 ? new(false, "QQ_SEND_PARTIAL") : result;
                sent++;
            }
            return new(true);
        }
        finally { _replyGate.Release(); }
    }

    internal static IReadOnlyList<string> SplitText(string text)
    {
        var chunks = new List<string>(3);
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(QqGatewayProtocol.TextChunkLength, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            chunks.Add(text.Substring(offset, count));
            offset += count;
        }
        return chunks;
    }

    private static void Notify(Action<QqTransportStatus> callback, QqTransportStatus status)
    {
        try { callback(status); }
        catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        if (_runTask is not null) await _runTask.ConfigureAwait(false);
        await _replyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _api?.InvalidateToken();
            _http.Dispose();
            _runCancellation?.Dispose();
            _lifetime.Dispose();
            lock (_messagesGate) { _received.Clear(); _receivedOrder.Clear(); }
        }
        finally { _replyGate.Release(); }
    }

    private sealed class GatewaySession
    {
        private long _sequence = -1;
        internal string? SessionId { get; set; }
        internal long Sequence { get => Interlocked.Read(ref _sequence); set => Interlocked.Exchange(ref _sequence, value); }
        internal void Reset() { SessionId = null; Sequence = -1; }
    }

    private sealed class HeartbeatState
    {
        private readonly object _gate = new();
        private long? _pendingSince;
        internal void MarkPending(TimeProvider time, TimeSpan? timeout = null)
        {
            lock (_gate)
            {
                if (_pendingSince is { } since && timeout is { } limit && time.GetElapsedTime(since) >= limit)
                    throw new QqTransportException("QQ_HEARTBEAT_TIMEOUT");
                _pendingSince ??= time.GetTimestamp();
            }
        }
        internal void Acknowledge() { lock (_gate) _pendingSince = null; }
    }
    private sealed class ReceivedMessage(string userOpenId, DateTimeOffset timestamp)
    {
        internal string UserOpenId { get; } = userOpenId;
        internal DateTimeOffset Timestamp { get; } = timestamp;
        internal int NextSequence { get; set; } = 1;
    }
}
