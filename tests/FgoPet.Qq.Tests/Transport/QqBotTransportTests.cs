using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using FgoPet.Plugin.Qq.Transport;
using Xunit;

namespace FgoPet.Qq.Tests.Transport;

public sealed class QqBotTransportTests
{
    [Fact]
    public async Task Unsupported_media_has_no_payload_and_can_only_receive_a_bound_passive_notice()
    {
        var clock = new QqTestClock();
        var http = new QqTestHttp();
        using var socket = new QqTestSocket();
        using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource<QqC2cMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new QqBotTransport(new QqTestCredentials(), http,
            (_, _, _) => Task.FromResult<WebSocket>(socket), clock);
        socket.Hello(); socket.Ready();
        socket.Push(new { op = 0, s = 2, t = "C2C_MESSAGE_CREATE", d = new {
            id = "synthetic-image", content = "untrusted caption must not reach a model",
            author = new { user_openid = "synthetic_user", bot = false },
            attachments = new[] { new { url = "https://untrusted.invalid/private-image" } },
            timestamp = clock.GetUtcNow().ToString("O") } });
        var running = transport.RunAsync(new("123456", "target"), (message, _) =>
        { received.TrySetResult(message); return Task.CompletedTask; }, _ => { }, stop.Token);
        try
        {
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(string.Empty, message.Content);
            Assert.True(message.IsUnsupportedMedia);
            Assert.True((await transport.ReplyTextAsync(message, "Text only", default)).Succeeded);
            Assert.All(http.Requests, request => Assert.DoesNotContain("untrusted.invalid", request.Body ?? ""));
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Identifies_with_the_minimum_c2c_intent_and_shutdown_closes_the_connection()
    {
        await using var fixture = new QqTransportFixture();
        var message = await fixture.StartWithMessageAsync();
        var identify = await fixture.Socket.SentAsync();
        Assert.Equal(2, identify.GetProperty("op").GetInt32());
        Assert.Equal(1 << 25, identify.GetProperty("d").GetProperty("intents").GetInt32());
        Assert.Equal("QQBot synthetic-access-token", identify.GetProperty("d").GetProperty("token").GetString());
        Assert.Equal(new[] { 0, 1 }, identify.GetProperty("d").GetProperty("shard").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal("synthetic_user", message.UserOpenId);
        fixture.Stop.Cancel();
        await fixture.Running!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(fixture.Statuses, status => status.State == QqTransportState.Stopped);
        Assert.Equal(WebSocketState.Aborted, fixture.Socket.State);
        Assert.DoesNotContain(fixture.Statuses, status => status.ToString().Contains("synthetic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replies_are_bound_to_an_admitted_user_and_split_without_losing_unicode_or_idempotency()
    {
        await using var fixture = new QqTransportFixture();
        var message = await fixture.StartWithMessageAsync();
        var text = new string('a', 4999) + "🙂" + new string('b', 6999);
        var forged = await fixture.Transport.ReplyTextAsync(message with { UserOpenId = "different_user" }, "hello", default);
        Assert.Equal("QQ_REPLY_NOT_ADMITTED", forged.ErrorCode);
        var result = await fixture.Transport.ReplyTextAsync(message, text, default);
        Assert.True(result.Succeeded);
        var bodies = fixture.Http.Requests.Where(IsReply).Select(request => JsonDocument.Parse(request.Body!)).ToArray();
        try
        {
            Assert.Equal(3, bodies.Length);
            var chunks = bodies.Select(body => body.RootElement.GetProperty("content").GetString()!).ToArray();
            Assert.Equal(text, string.Concat(chunks));
            Assert.All(chunks, chunk => Assert.True(QqGatewayProtocol.IsValidUtf16(chunk)));
            Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 5000));
            Assert.Equal(new[] { 1, 2, 3 }, bodies.Select(body => body.RootElement.GetProperty("msg_seq").GetInt32()));
            Assert.All(bodies, body => Assert.Equal(message.MessageId, body.RootElement.GetProperty("msg_id").GetString()));
            Assert.All(bodies, body => Assert.Equal(0, body.RootElement.GetProperty("msg_type").GetInt32()));
        }
        finally { foreach (var body in bodies) body.Dispose(); }
        Assert.True((await fixture.Transport.ReplyTextAsync(message, "fourth reply", default)).Succeeded);
        Assert.Equal("QQ_REPLY_LIMIT", (await fixture.Transport.ReplyTextAsync(message, "fifth reply", default)).ErrorCode);
        Assert.Equal(4, fixture.Http.Requests.Count(IsReply));
        Assert.Equal(1, fixture.Credentials.Reads);
    }

    [Fact]
    public async Task Expired_reply_is_refused_without_falling_back_to_proactive_send()
    {
        await using var fixture = new QqTransportFixture();
        var message = await fixture.StartWithMessageAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var result = await fixture.Transport.ReplyTextAsync(message, "reply", default);
        Assert.Equal("QQ_REPLY_EXPIRED", result.ErrorCode);
        Assert.Empty(fixture.Http.Requests.Where(IsReply));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_reuses_session_sequence_and_replayed_message_is_not_delivered_twice(bool connectionExpired)
    {
        var clock = new QqTestClock();
        var http = new QqTestHttp();
        var credentials = new QqTestCredentials();
        using var first = new QqTestSocket();
        using var second = new QqTestSocket();
        var sockets = new ConcurrentQueue<QqTestSocket>(new[] { first, second });
        await using var transport = new QqBotTransport(credentials, http,
            (_, _, _) => Task.FromResult<WebSocket>(sockets.TryDequeue(out var socket) ? socket : throw new InvalidOperationException()), clock);
        using var stop = new CancellationTokenSource();
        var received = new ConcurrentQueue<string>();
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Hello(); first.Ready(); first.Text(clock, "first", sequence: 2);
        second.Hello();
        var running = transport.RunAsync(new("123456", "target"), (message, _) =>
        {
            received.Enqueue(message.MessageId);
            if (message.MessageId == "first") initial.TrySetResult();
            if (message.MessageId == "second") replayed.TrySetResult();
            return Task.CompletedTask;
        }, _ => { }, stop.Token);
        await initial.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (connectionExpired) first.PushClose(4009);
        else first.Push(new { op = 7 });
        var resume = await second.SentAsync();
        Assert.Equal(6, resume.GetProperty("op").GetInt32());
        Assert.Equal("synthetic-session", resume.GetProperty("d").GetProperty("session_id").GetString());
        Assert.Equal(2, resume.GetProperty("d").GetProperty("seq").GetInt64());
        Assert.Equal(1, credentials.Reads);
        second.Text(clock, "first", sequence: 2);
        second.Text(clock, "second", sequence: 3);
        second.Push(new { op = 0, s = 4, t = "RESUMED", d = "" });
        await replayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first", "second" }, received.ToArray());
    }

    [Fact]
    public async Task Invalid_session_close_refreshes_token_and_identifies_instead_of_resuming()
    {
        var clock = new QqTestClock();
        var credentials = new QqTestCredentials();
        var http = new QqTestHttp();
        using var first = new QqTestSocket();
        using var second = new QqTestSocket();
        var sockets = new ConcurrentQueue<QqTestSocket>(new[] { first, second });
        await using var transport = new QqBotTransport(credentials, http,
            (_, _, _) => Task.FromResult<WebSocket>(sockets.TryDequeue(out var socket) ? socket : throw new InvalidOperationException()), clock);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Hello(); first.Ready(); second.Hello();
        var running = transport.RunAsync(new("123456", "target"), (_, _) => Task.CompletedTask,
            status => { if (status.State == QqTransportState.Connected) ready.TrySetResult(); }, stop.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.PushClose(4007);
        var identify = await second.SentAsync();
        Assert.Equal(2, identify.GetProperty("op").GetInt32());
        Assert.Equal(2, credentials.Reads);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Heartbeat_carries_latest_sequence_and_missing_ack_reconnects()
    {
        var clock = new QqTestClock();
        using var socket = new QqTestSocket();
        using var stop = new CancellationTokenSource();
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var transport = new QqBotTransport(new QqTestCredentials(), new QqTestHttp(),
            (_, _, _) => Task.FromResult<WebSocket>(socket), clock);
        socket.Hello(1000); socket.Ready(8);
        var running = transport.RunAsync(new("123456", "target"), (_, _) => Task.CompletedTask, status =>
        {
            if (status.ErrorCode == "QQ_HEARTBEAT_TIMEOUT") timedOut.TrySetResult();
        }, stop.Token);
        Assert.Equal(2, (await socket.SentAsync()).GetProperty("op").GetInt32());
        var first = await socket.SentAsync();
        Assert.Equal(1, first.GetProperty("op").GetInt32());
        Assert.Equal(8, first.GetProperty("d").GetInt64());
        socket.Push(new { op = 11 });
        var second = await socket.SentAsync();
        Assert.Equal(1, second.GetProperty("op").GetInt32());
        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Fatal_close_stops_and_cancellation_interrupts_pending_http_without_leaking_errors()
    {
        var clock = new QqTestClock();
        using var socket = new QqTestSocket();
        var statuses = new ConcurrentQueue<QqTransportStatus>();
        await using (var transport = new QqBotTransport(new QqTestCredentials(), new QqTestHttp(),
            (_, _, _) => Task.FromResult<WebSocket>(socket), clock))
        {
            socket.PushClose(4915);
            await transport.RunAsync(new("123456", "target"), (_, _) => Task.CompletedTask, statuses.Enqueue, default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(statuses, status => status.State == QqTransportState.Faulted && status.ErrorCode == "QQ_GATEWAY_ACCESS_DENIED");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new QqTestHttp
        {
            Respond = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("synthetic private content");
            }
        };
        using var stop = new CancellationTokenSource();
        await using var cancelled = new QqBotTransport(new QqTestCredentials(), pending,
            (_, _, _) => throw new InvalidOperationException(), clock);
        var running = cancelled.RunAsync(new("123456", "target"), (_, _) => Task.CompletedTask, statuses.Enqueue, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(QqTransportState.Stopped, statuses.Last().State);
        Assert.DoesNotContain(statuses, status => status.ToString().Contains("synthetic", StringComparison.Ordinal));
    }

    private static bool IsReply(QqTestRequest request) => request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal);

    [Theory]
    [InlineData(4001)]
    [InlineData(4002)]
    [InlineData(4010)]
    [InlineData(4011)]
    [InlineData(4012)]
    [InlineData(4013)]
    [InlineData(4014)]
    public async Task Nonrecoverable_protocol_or_configuration_close_faults_without_reconnect(int closeCode)
    {
        using var socket = new QqTestSocket();
        using var stop = new CancellationTokenSource();
        var outcome = new TaskCompletionSource<QqTransportStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        await using var transport = new QqBotTransport(new QqTestCredentials(), new QqTestHttp(),
            (_, _, _) => { Interlocked.Increment(ref connections); return Task.FromResult<WebSocket>(socket); }, new QqTestClock());
        socket.Hello(); socket.Ready(); socket.PushClose(closeCode);
        var running = transport.RunAsync(new("123456", "target"), (_, _) => Task.CompletedTask,
            status => { if (status.State is QqTransportState.Faulted or QqTransportState.Reconnecting) outcome.TrySetResult(status); }, stop.Token);
        try
        {
            Assert.Equal(QqTransportState.Faulted, (await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, connections);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
