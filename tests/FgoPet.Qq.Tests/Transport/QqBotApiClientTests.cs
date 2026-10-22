using System.Net;
using System.Text;
using System.Text.Json;
using FgoPet.Plugin.Qq.Transport;
using Xunit;

namespace FgoPet.Qq.Tests.Transport;

public sealed class QqBotApiClientTests
{
    [Fact]
    public async Task Credentials_are_read_from_the_injected_store_and_token_is_cached_then_refreshed()
    {
        var credentials = new QqTestCredentials();
        var clock = new QqTestClock();
        var handler = new QqTestHttp();
        using var http = new HttpClient(handler);
        var api = new QqBotApiClient(http, credentials, new("123456", "protected-target"), clock);
        await api.GetAccessTokenAsync(default);
        await api.GetAccessTokenAsync(default);
        Assert.Equal(1, credentials.Reads);
        clock.Advance(TimeSpan.FromSeconds(7141));
        await api.GetAccessTokenAsync(default);
        Assert.Equal(2, credentials.Reads);
        Assert.Equal("protected-target", credentials.Target);
        Assert.All(handler.Requests, request => Assert.Equal("https://api.bot.qq.com/app/getAppAccessToken", request.Uri.ToString()));
        using var body = JsonDocument.Parse(handler.Requests.First().Body!);
        Assert.Equal("123456", body.RootElement.GetProperty("appId").GetString());
        Assert.Equal(credentials.Value, body.RootElement.GetProperty("clientSecret").GetString());
    }

    [Fact]
    public async Task Business_failure_inside_http_200_does_not_become_a_token_or_expose_response_text()
    {
        var handler = new QqTestHttp
        {
            Respond = (_, _) => Task.FromResult(QqTestHttp.Json("""{"code":100016,"message":"synthetic private diagnostic"}"""))
        };
        using var http = new HttpClient(handler);
        var api = new QqBotApiClient(http, new QqTestCredentials(), new("123456", "target"), new QqTestClock());
        var error = await Assert.ThrowsAsync<QqTransportException>(() => api.GetAccessTokenAsync(default));
        Assert.True(error.Terminal);
        Assert.Equal("QQ_AUTHENTICATION_FAILED", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Redirect_and_untrusted_gateway_address_are_rejected_without_connecting()
    {
        var handler = new QqTestHttp
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/gateway"
                ? QqTestHttp.Json("""{"url":"wss://api.bot.qq.com.evil.invalid/"}""") : QqTestHttp.DefaultResponse(request))
        };
        using var http = new HttpClient(handler);
        var api = new QqBotApiClient(http, new QqTestCredentials(), new("123456", "target"), new QqTestClock());
        var addressError = await Assert.ThrowsAsync<QqTransportException>(() => api.GetGatewayAsync("synthetic-token", default));
        Assert.Equal("QQ_GATEWAY_ADDRESS_DENIED", addressError.Code);
        handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://example.invalid/secret-collector") } });
        var redirectError = await Assert.ThrowsAsync<QqTransportException>(() => api.GetAccessTokenAsync(default));
        Assert.Equal("QQ_REDIRECT_DENIED", redirectError.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Explicit_unauthorized_reply_refreshes_once_and_reuses_the_reply_identity()
    {
        var sends = 0;
        var handler = new QqTestHttp
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                && Interlocked.Increment(ref sends) == 1
                ? QqTestHttp.Json("{}", HttpStatusCode.Unauthorized) : QqTestHttp.DefaultResponse(request))
        };
        var credentials = new QqTestCredentials();
        using var http = new HttpClient(handler);
        var api = new QqBotApiClient(http, credentials, new("123456", "target"), new QqTestClock());
        var result = await api.ReplyAsync("synthetic_user", "original-message", "hello", 2, default);
        Assert.True(result.Succeeded);
        Assert.Equal(2, credentials.Reads);
        var replies = handler.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, replies.Length);
        Assert.Equal(replies[0].Body, replies[1].Body);
        Assert.All(replies, request => Assert.Equal("QQBot synthetic-access-token", request.Authorization));
        using var body = JsonDocument.Parse(replies[0].Body!);
        Assert.Equal("original-message", body.RootElement.GetProperty("msg_id").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("msg_seq").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_response_or_server_failure_is_unknown_and_is_not_retried(bool throwNetwork)
    {
        var handler = new QqTestHttp
        {
            Respond = (request, _) =>
            {
                if (!request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                    return Task.FromResult(QqTestHttp.DefaultResponse(request));
                if (throwNetwork) throw new HttpRequestException("synthetic private response");
                return Task.FromResult(QqTestHttp.Json("{}", HttpStatusCode.InternalServerError));
            }
        };
        using var http = new HttpClient(handler);
        var api = new QqBotApiClient(http, new QqTestCredentials(), new("123456", "target"), new QqTestClock());
        var result = await api.ReplyAsync("synthetic_user", "message", "hello", 1, default);
        Assert.False(result.Succeeded);
        Assert.Equal("QQ_SEND_UNKNOWN", result.ErrorCode);
        Assert.Single(handler.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Response_limit_is_enforced_without_a_content_length_header()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string('x', 20_000))))
        };
        Assert.Null(response.Content.Headers.ContentLength);
        var error = await Assert.ThrowsAsync<QqTransportException>(() => QqBotApiClient.ReadJsonAsync(response, 16_384, default));
        Assert.Equal("QQ_RESPONSE_TOO_LARGE", error.Code);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
