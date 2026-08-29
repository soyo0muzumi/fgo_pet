using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.Core.Tests.Dialogue;

public sealed class PromptContractTests
{
    [Fact]
    public void Prompt_context_keeps_runtime_messages_and_approved_data_separate()
    {
        var context = new ContentContextKey("800100", "official.mash", "1.1.0", "casual", "persona-2", "knowledge-1");
        var prompt = new PromptContext(
            context,
            new PersonaBundle("800100", "official.mash", "1.1.0", "persona-2", "玛修的核心设定", Array.Empty<PersonaAppearanceOverlay>()),
            new[] { new KnowledgeEntry("profile-1", "800100", "profile", "可靠的设定摘要", "approved") },
            new[] { new StoredMemory("memory-1", "800100", "用户喜欢番茄钟", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) },
            "focus_running",
            new[] { new PromptMessage(ChatMessageRole.User, "上一条消息") },
            "请继续陪我工作");

        Assert.Equal("800100", prompt.ContentContext.ServantId);
        Assert.Single(prompt.Knowledge);
        Assert.Single(prompt.Memories);
        Assert.Equal("focus_running", prompt.RuntimeState);
        Assert.Equal(ChatMessageRole.User, Assert.Single(prompt.Messages).Role);
    }

    [Fact]
    public void Chat_request_rejects_tool_call_fields_by_exposing_only_messages()
    {
        var request = new ChatRequest(
            "800100",
            "conversation-1",
            new[] { new PromptMessage(ChatMessageRole.User, "你好") });

        Assert.Equal("800100", request.ServantId);
        Assert.Empty(request.Metadata);
    }
}
