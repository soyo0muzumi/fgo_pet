using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FgoPet.Core.Secrets;
using FgoPet.Plugin.Qq.Transport;

namespace FgoPet.Qq.Tests.Transport;

internal sealed class QqTestCredentials : ICredentialReader
{
    internal int Reads;
    internal string? Value { get; set; } = "synthetic-qq-secret";
    internal string? Target { get; private set; }
    public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Reads);
        Target = target;
        return Task.FromResult(Value);
    }
}

internal sealed class QqTestClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

internal sealed record QqTestRequest(string Method, Uri Uri, string? Authorization, string? Body);

internal sealed class QqTestHttp : HttpMessageHandler
{
    internal ConcurrentQueue<QqTestRequest> Requests { get; } = new();
    internal Func<QqTestRequest, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = new QqTestRequest(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Enqueue(snapshot);
        if (Respond is not null) return await Respond(snapshot, cancellationToken);
        return DefaultResponse(snapshot);
    }

    internal static HttpResponseMessage DefaultResponse(QqTestRequest request) => request.Uri.AbsolutePath switch
    {
        "/app/getAppAccessToken" => Json("""{"access_token":"synthetic-access-token","expires_in":"7200"}"""),
        "/gateway" => Json("""{"url":"wss://api.bot.qq.com/websocket/"}"""),
        _ => Json("""{"id":"synthetic-sent-message"}""")
    };

    internal static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
}

internal sealed class QqTestSocket : WebSocket
{
    private readonly Channel<SocketFrame> _incoming = Channel.CreateUnbounded<SocketFrame>();
    private readonly Channel<JsonElement> _outgoing = Channel.CreateUnbounded<JsonElement>();
    private SocketFrame? _current;
    private int _offset;
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    internal void Push(object payload) => PushBytes(JsonSerializer.SerializeToUtf8Bytes(payload));
    internal void PushJson(string payload) => PushBytes(Encoding.UTF8.GetBytes(payload));
    internal void PushBytes(byte[] payload, WebSocketMessageType type = WebSocketMessageType.Text)
        => _incoming.Writer.TryWrite(new(payload, type, null));
    internal void PushClose(int code) => _incoming.Writer.TryWrite(new([], WebSocketMessageType.Close, (WebSocketCloseStatus)code));
    internal void Hello(int interval = 45_000) => Push(new { op = 10, d = new { heartbeat_interval = interval } });
    internal void Ready(long sequence = 1) => Push(new { op = 0, s = sequence, t = "READY", d = new { session_id = "synthetic-session" } });
    internal void Text(QqTestClock clock, string id = "synthetic-message", string user = "synthetic_user", long sequence = 2)
        => Push(new
        {
            op = 0, s = sequence, t = "C2C_MESSAGE_CREATE", d = new
            {
                id, author = new { user_openid = user, bot = false }, content = "synthetic private text", message_type = 0,
                timestamp = clock.GetUtcNow().ToString("O")
            }
        });

    internal async Task<JsonElement> SentAsync(TimeSpan? timeout = null)
        => await _outgoing.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(5));

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _incoming.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { Abort(); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { Abort(); return Task.CompletedTask; }
    public override void Dispose() => Abort();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        try { _current ??= await _incoming.Reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException) { throw new WebSocketException(); }
        var current = _current;
        if (current.Type == WebSocketMessageType.Close)
        {
            _closeStatus = current.CloseStatus;
            _state = WebSocketState.CloseReceived;
            _current = null;
            return new(0, WebSocketMessageType.Close, true, _closeStatus, null);
        }
        var count = Math.Min(buffer.Count, current.Bytes.Length - _offset);
        current.Bytes.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
        _offset += count;
        var end = _offset == current.Bytes.Length;
        if (end) { _current = null; _offset = 0; }
        return new(count, current.Type, end);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(buffer.AsMemory());
        _outgoing.Writer.TryWrite(document.RootElement.Clone());
        return Task.CompletedTask;
    }

    private sealed record SocketFrame(byte[] Bytes, WebSocketMessageType Type, WebSocketCloseStatus? CloseStatus);
}

internal sealed class QqTransportFixture : IAsyncDisposable
{
    internal QqTestClock Clock { get; } = new();
    internal QqTestCredentials Credentials { get; } = new();
    internal QqTestHttp Http { get; } = new();
    internal QqTestSocket Socket { get; } = new();
    internal QqBotTransport Transport { get; }
    internal CancellationTokenSource Stop { get; } = new();
    internal ConcurrentQueue<QqTransportStatus> Statuses { get; } = new();
    internal TaskCompletionSource<QqC2cMessage> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? Running { get; private set; }

    internal QqTransportFixture()
    {
        Transport = new(Credentials, Http, (_, _, _) => Task.FromResult<WebSocket>(Socket), Clock);
    }

    internal async Task<QqC2cMessage> StartWithMessageAsync()
    {
        Socket.Hello(); Socket.Ready(); Socket.Text(Clock);
        Running = Transport.RunAsync(new("123456", "FgoPet/QQ/test"), (message, _) =>
        {
            Received.TrySetResult(message);
            return Task.CompletedTask;
        }, Statuses.Enqueue, Stop.Token);
        return await Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        Stop.Cancel();
        await Transport.DisposeAsync();
        Stop.Dispose();
    }
}
