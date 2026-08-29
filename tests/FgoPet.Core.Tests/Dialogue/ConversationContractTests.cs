using FgoPet.Core.Dialogue;
using Xunit;

namespace FgoPet.Core.Tests.Dialogue;

public sealed class ConversationContractTests
{
    [Fact]
    public void Content_context_includes_servant_package_version_and_appearance()
    {
        var key = new ContentContextKey("800100", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1");

        Assert.Equal("800100", key.ServantId);
        Assert.Equal("official.mash", key.PackageId);
        Assert.Equal("1.1.0", key.PackageVersion);
        Assert.Equal("casual", key.AppearanceId);
        Assert.Equal("persona-2", key.PersonaVersion);
        Assert.Equal("knowledge-1", key.KnowledgeVersion);
    }

    [Fact]
    public void Chat_message_preserves_role_status_and_content_context()
    {
        var context = new ContentContextKey("800100", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1");
        var message = new ChatMessage(
            "message-1",
            "conversation-1",
            "800100",
            ChatMessageRole.User,
            "请陪我工作",
            ChatMessageStatus.Completed,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            context,
            1);

        Assert.Equal(ChatMessageRole.User, message.Role);
        Assert.Equal(ChatMessageStatus.Completed, message.Status);
        Assert.Equal(context, message.ContentContext);
        Assert.Equal(1, message.Sequence);
    }

    [Fact]
    public void Content_context_rejects_missing_identity()
    {
        Assert.Throws<ArgumentException>(() =>
            new ContentContextKey("", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1"));
    }
}
