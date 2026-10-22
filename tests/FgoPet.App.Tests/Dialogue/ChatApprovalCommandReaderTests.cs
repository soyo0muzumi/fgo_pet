using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ChatApprovalCommandReaderTests
{
    private const string Identity = "\"sessionId\":\"session\",\"servantId\":\"role\",\"conversationId\":\"conversation\"";
    private const string Binding = ",\"runId\":\"run\",\"requestId\":\"approval\",\"revision\":4";
    [Theory]
    [InlineData("review")]
    [InlineData("deny")]
    [InlineData("cancel")]
    public void Only_closed_correlated_review_refusal_or_cancel_messages_are_read(string action)
    {
        Assert.True(ChatWebCommandReader.TryRead(Message("chat.approval." + action, "{" + Identity + Binding + "}"), out var command, out _));
        Assert.Equal("approval", command!.InputRequestId); Assert.Equal(4L, command.Revision);
        Assert.Null(command.Text); Assert.True(command.Answers.IsDefault);
    }
    [Theory]
    [InlineData("chat.approval.allow", "")]
    [InlineData("chat.approval.review", ",\"allow\":true")]
    [InlineData("chat.approval.review", ",\"decision\":\"Allow\"")]
    [InlineData("chat.approval.review", ",\"arguments\":{\"changed\":true}")]
    [InlineData("chat.approval.review", ",\"scope\":\"other\"")]
    [InlineData("chat.approval.review", ",\"requestId\":\"duplicate\"")]
    public void Untrusted_allow_or_mutable_arguments_and_duplicate_fields_are_rejected(string type, string extra)
    {
        Assert.False(ChatWebCommandReader.TryRead(Message(type, "{" + Identity + Binding + extra + "}"), out _, out var code));
        Assert.Equal("CHAT_COMMAND_INVALID", code);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"4\"")]
    public void Non_positive_or_non_integer_revisions_are_rejected(string revision)
    {
        Assert.False(ChatWebCommandReader.TryRead(Message("chat.approval.review", "{" + Identity
            + ",\"runId\":\"run\",\"requestId\":\"approval\",\"revision\":" + revision + "}"), out _, out _));
    }
    private static WebSurfaceMessage Message(string type, string json) => new(type, "transport", JsonDocument.Parse(json).RootElement.Clone());
}
