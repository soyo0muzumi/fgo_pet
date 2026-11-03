using System.Text;
using FgoPet.Plugin.Qq.Transport;
using Xunit;

namespace FgoPet.Qq.Tests.Transport;

public sealed class QqImageTests
{
    [Theory]
    [InlineData("https://multimedia.nt.qq.com.cn/download?fileid=fixture", "image/png", true)]
    [InlineData("https://gchat.qpic.cn/fixture", "image/jpeg", true)]
    [InlineData("http://gchat.qpic.cn/fixture", "image/png", false)]
    [InlineData("https://gchat.qpic.cn.evil.invalid/image", "image/png", false)]
    [InlineData("https://127.0.0.1/image", "image/png", false)]
    [InlineData("https://gchat.qpic.cn/image", "application/octet-stream", false)]
    public void Image_envelope_preserves_caption_only_for_supported_official_attachment(string url, string type, bool allowed)
    {
        var frame = QqGatewayProtocol.Parse(Encoding.UTF8.GetBytes($$$"""
        {"op":0,"t":"C2C_MESSAGE_CREATE","d":{"id":"image-one","author":{"user_openid":"owner"},
        "timestamp":"2026-11-03T00:00:00Z","content":"describe this",
        "attachments":[{"url":"{{{url}}}","content_type":"{{{type}}}"}]}}
        """));
        var message = QqGatewayProtocol.ReadImageMessage(frame);
        Assert.Equal(allowed, message is not null);
        if (message is not null) { Assert.Equal("describe this", message.Content); Assert.NotNull(message.Image); }
    }
}
