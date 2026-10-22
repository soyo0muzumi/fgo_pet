using System.Text;
using FgoPet.Plugin.Qq.Transport;
using Xunit;

namespace FgoPet.Qq.Tests.Transport;

public sealed class QqGatewayProtocolTests
{
    [Theory]
    [InlineData("C2C_MESSAGE_CREATE", "synthetic_user", false, "[{\"url\":\"https://untrusted.invalid/image\"}]", true, false)]
    [InlineData("GROUP_AT_MESSAGE_CREATE", "synthetic_user", false, "[{}]", false, false)]
    [InlineData("C2C_MESSAGE_CREATE", "../invalid", false, "[{}]", false, false)]
    [InlineData("C2C_MESSAGE_CREATE", "synthetic_user", true, "[{}]", false, false)]
    [InlineData("C2C_MESSAGE_CREATE", "synthetic_user", false, "[]", false, true)]
    [InlineData("C2C_MESSAGE_CREATE", "synthetic_user", false, "{}", false, false)]
    public void Media_notice_envelope_validates_sender_and_discards_all_attachment_content(
        string eventType, string openId, bool bot, string attachments, bool admitted, bool textAdmitted)
    {
        var frame = QqGatewayProtocol.Parse(Encoding.UTF8.GetBytes($$$"""
            {"op":0,"s":4,"t":"{{{eventType}}}","d":{"id":"synthetic-media","content":"untrusted caption",
            "author":{"user_openid":"{{{openId}}}","bot":{{{bot.ToString().ToLowerInvariant()}}}},
            "attachments":{{{attachments}}},"timestamp":"2026-10-21T12:00:00Z"}}
            """));
        Assert.Equal(textAdmitted, QqGatewayProtocol.ReadTextMessage(frame) is not null);
        var message = QqGatewayProtocol.ReadUnsupportedMediaMessage(frame);
        Assert.Equal(admitted, message is not null);
        if (message is not null) { Assert.True(message.IsUnsupportedMedia); Assert.Equal(string.Empty, message.Content); }
    }

    [Theory]
    [InlineData("wss://api.bot.qq.com/websocket/", true)]
    [InlineData("wss://api.sgroup.qq.com/websocket/", true)]
    [InlineData("ws://api.bot.qq.com/websocket/", false)]
    [InlineData("wss://api.bot.qq.com:8443/websocket/", false)]
    [InlineData("wss://api.bot.qq.com.evil.invalid/websocket/", false)]
    [InlineData("wss://api.bot.qq.com@evil.invalid/websocket/", false)]
    [InlineData("wss://user@api.bot.qq.com/websocket/", false)]
    [InlineData("wss://127.0.0.1/websocket/", false)]
    [InlineData("wss://api.bot.qq.com/websocket/#fragment", false)]
    public void Gateway_requires_an_exact_official_tls_origin(string address, bool allowed)
        => Assert.Equal(allowed, QqGatewayProtocol.IsGatewayAllowed(new Uri(address)));

    [Theory]
    [InlineData("GROUP_AT_MESSAGE_CREATE", "0", "[]", "synthetic_user", false)]
    [InlineData("C2C_MESSAGE_CREATE", "3", "[]", "synthetic_user", false)]
    [InlineData("C2C_MESSAGE_CREATE", "\"0\"", "[]", "synthetic_user", false)]
    [InlineData("C2C_MESSAGE_CREATE", "0", "[{\"url\":\"https://example.invalid/private\"}]", "synthetic_user", false)]
    [InlineData("C2C_MESSAGE_CREATE", "0", "[]", "../another-user", false)]
    [InlineData("C2C_MESSAGE_CREATE", "0", "[]", "synthetic_user", true)]
    public void Only_direct_plain_text_with_a_bounded_openid_is_admitted(string eventType, string kind,
        string attachments, string openId, bool admitted)
    {
        var frame = QqGatewayProtocol.Parse(Encoding.UTF8.GetBytes($$$"""
            {"op":0,"s":4,"t":"{{{eventType}}}","d":{"id":"synthetic-message","content":"你好",
            "author":{"user_openid":"{{{openId}}}","bot":false},"message_type":{{{kind}}},
            "attachments":{{{attachments}}},"timestamp":"2026-10-21T12:00:00Z"}}
            """));
        Assert.Equal(admitted, QqGatewayProtocol.ReadTextMessage(frame) is not null);
    }

    [Theory]
    [InlineData("[{\"message_type\":0,\"content\":\"text metadata\"}]", true)]
    [InlineData("[{\"content\":\"text metadata\"}]", true)]
    [InlineData("[{\"message_type\":103,\"content\":\"quoted text\"}]", false)]
    [InlineData("[{\"content\":\"media\",\"attachments\":[{\"url\":\"https://example.invalid/image\"}]}]", false)]
    public void Optional_plain_text_elements_do_not_reject_a_normal_message_or_inject_quoted_content(string elements, bool admitted)
    {
        var frame = QqGatewayProtocol.Parse(Encoding.UTF8.GetBytes($$$"""
            {"op":0,"s":4,"t":"C2C_MESSAGE_CREATE","d":{"id":"synthetic-message","content":"top-level text",
            "author":{"user_openid":"synthetic_user"},"message_type":0,"msg_elements":{{{elements}}},
            "timestamp":"2026-10-21T12:00:00Z"}}
            """));
        var message = QqGatewayProtocol.ReadTextMessage(frame);
        Assert.Equal(admitted, message is not null);
        if (admitted) Assert.Equal("top-level text", message!.Content);
    }

    [Fact]
    public async Task Fragmented_frames_are_reassembled_and_oversized_frames_are_rejected()
    {
        using var good = new QqTestSocket();
        good.PushJson("{\"op\":0,\"s\":2,\"t\":\"IGNORED\",\"d\":{\"text\":\"" + new string('x', 16_000) + "\"}}");
        var parsed = await QqBotTransport.ReadFrameAsync(good, CancellationToken.None);
        Assert.Equal(2L, parsed.Sequence);
        using var large = new QqTestSocket();
        large.PushBytes(new byte[QqGatewayProtocol.MaxFrameBytes + 1]);
        var error = await Assert.ThrowsAsync<QqTransportException>(() => QqBotTransport.ReadFrameAsync(large, CancellationToken.None));
        Assert.Equal("QQ_FRAME_TOO_LARGE", error.Code);
    }

    [Fact]
    public void Deep_or_malformed_json_has_only_a_safe_error_code()
    {
        var text = "{\"op\":0,\"d\":" + new string('[', 30) + "\"synthetic private content\"" + new string(']', 30) + "}";
        var error = Assert.Throws<QqTransportException>(() => QqGatewayProtocol.Parse(Encoding.UTF8.GetBytes(text)));
        Assert.Equal("QQ_FRAME_INVALID", error.Message);
        Assert.Null(error.InnerException);
    }
}
