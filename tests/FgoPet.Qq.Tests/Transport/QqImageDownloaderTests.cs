using System.Net;
using System.Net.Http.Headers;
using FgoPet.Plugin.Qq.Transport;
using Xunit;

namespace FgoPet.Qq.Tests.Transport;

public sealed class QqImageDownloaderTests
{
    [Theory]
    [InlineData(200, "image/png", 8, true)]
    [InlineData(302, "image/png", 8, false)]
    [InlineData(200, "image/jpeg", 8, false)]
    [InlineData(200, "text/html", 8, false)]
    [InlineData(200, "image/png", 0, false)]
    [InlineData(200, "image/png", 8388609, false)]
    public async Task Download_is_bounded_and_never_sends_bot_credentials(int status, string type, int size, bool expected)
    {
        using var handler = new QqTestHttp { Respond = (_, _) => {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(new byte[size]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            response.Headers.Location = new Uri("https://127.0.0.1/private");
            return Task.FromResult(response);
        } };
        using var downloader = new QqImageDownloader(handler);
        var result = await downloader.DownloadAsync(new("https://gchat.qpic.cn/synthetic", "image/png"), default);
        Assert.Equal(expected, result is not null);
        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task Untrusted_origin_is_denied_without_network_and_user_cancellation_propagates()
    {
        using var handler = new QqTestHttp();
        using var downloader = new QqImageDownloader(handler);
        Assert.Null(await downloader.DownloadAsync(new("https://example.invalid/image", "image/png"), default));
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            new("https://gchat.qpic.cn/image", "image/png"), new(true)));
    }
}
