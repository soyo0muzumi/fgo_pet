using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ChatPresentationBridgeTests
{
    [Theory]
    [InlineData("composer", false, 160)]
    [InlineData("conversation", true, 360)]
    public void Presentation_payload_is_closed_and_bounded(string view, bool wide, double height)
    {
        var message = new WebSurfaceMessage("chat.host", "fixture", JsonSerializer.SerializeToElement(new
        { sessionId = "s", servantId = "r", conversationId = "", action = "setPresentation", view, wide, composerHeight = height }));
        Assert.True(ChatWebCommandReader.TryRead(message, out var command, out _));
        Assert.Equal(view, command!.View); Assert.Equal(height, command.ComposerHeight);
    }

    [Theory]
    [InlineData("\"view\":\"browser\",\"wide\":false")]
    [InlineData("\"view\":\"composer\",\"wide\":false,\"composerHeight\":159")]
    [InlineData("\"view\":\"composer\",\"wide\":false,\"composerHeight\":361")]
    [InlineData("\"view\":\"composer\",\"wide\":false,\"path\":\"private\"")]
    [InlineData("\"view\":\"composer\",\"wide\":\"true\"")]
    public void Rejects_unrecognized_view_geometry_and_extra_fields(string fields)
    {
        using var json = JsonDocument.Parse("{\"sessionId\":\"s\",\"servantId\":\"r\",\"conversationId\":\"\",\"action\":\"setPresentation\"," + fields + "}");
        Assert.False(ChatWebCommandReader.TryRead(new("chat.host", "fixture", json.RootElement), out _, out _));
    }

    [Fact]
    public void Model_refresh_does_not_accept_credentials_endpoint_or_selected_model()
    {
        using var valid = JsonDocument.Parse("{\"sessionId\":\"s\",\"servantId\":\"r\",\"conversationId\":\"\",\"action\":\"refreshModels\"}");
        Assert.True(ChatWebCommandReader.TryRead(new("chat.host", "fixture", valid.RootElement), out _, out _));
        using var invalid = JsonDocument.Parse("{\"sessionId\":\"s\",\"servantId\":\"r\",\"conversationId\":\"\",\"action\":\"refreshModels\",\"targetId\":\"other\"}");
        Assert.False(ChatWebCommandReader.TryRead(new("chat.host", "fixture", invalid.RootElement), out _, out _));
    }
}
