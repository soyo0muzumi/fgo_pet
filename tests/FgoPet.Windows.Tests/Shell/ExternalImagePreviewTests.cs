using System.IO;
using System.Net;
using System.Net.Http;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class ExternalImagePreviewTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.2")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void Non_public_addresses_are_rejected(string address) =>
        Assert.False(ExternalImagePreview.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://localhost/p.png")]
    [InlineData("https://127.0.0.1/p.png")]
    [InlineData("http://example.org/p.png")]
    [InlineData("https://user:secret@example.org/p.png")]
    [InlineData("https://example.org:444/p.png")]
    [InlineData("file:///C:/p.png")]
    public async Task Unsafe_urls_never_reach_transport(string url)
    {
        using var loader = new ExternalImagePreview(new FixtureHandler(_ => throw new Xunit.Sdk.XunitException("Unexpected request")));
        Assert.Null(await loader.LoadAsync(new Uri(url), CancellationToken.None));
    }

    [Fact]
    public Task Png_is_decoded_and_returned_as_metadata_free_png() => StaRunner.RunAsync(async () =>
    {
        using var loader = new ExternalImagePreview(new FixtureHandler(_ => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Png) }));
        var image = await loader.LoadAsync(new Uri("https://example.org/image"), CancellationToken.None);
        Assert.StartsWith("data:image/png;base64,", image);
    });

    [Theory]
    [InlineData(302, false)]
    [InlineData(200, false)]
    [InlineData(200, true)]
    public async Task Redirect_invalid_and_oversized_images_are_rejected(int status, bool oversized)
    {
        using var loader = new ExternalImagePreview(new FixtureHandler(_ => new((HttpStatusCode)status)
        { Content = new ByteArrayContent(oversized ? new byte[4 * 1024 * 1024 + 1] : "<svg/>"u8.ToArray()) }));
        Assert.Null(await loader.LoadAsync(new Uri("https://example.org/image"), CancellationToken.None));
    }

    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
