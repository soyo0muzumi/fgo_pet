using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    // Expression realizes the final answer. On a tool step it competes with tool selection, so the
    // hint must be withheld there and offered again for the final output step.
    [Fact]
    public async Task Expression_hint_is_withheld_from_tool_steps_and_kept_for_final_output()
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(
            ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Final("已读取。"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            useNative: true, composer: new PromptComposer(null, null, ExpressionMode.Full));

        Assert.Equal(ConversationSendStatus.Completed,
            (await host.Orchestrator.SendAsync(role, "今天好累呀", default, new(ProjectId))).Status);

        // Tool steps must not carry Expression; the final output step must.
        Assert.True(provider.Requests.Count >= 2);
        for (var index = 0; index < provider.Requests.Count; index++)
        {
            var request = provider.Requests[index];
            var hasHint = request.ModelMessages.Any(
                message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            var isFinal = request.Tools is null;
            Assert.True(isFinal || !hasHint,
                $"request {index}: tools={(request.Tools is null ? "none" : request.Tools.Count.ToString())} hint={hasHint}");
        }
        Assert.Contains(provider.Requests[^1].ModelMessages,
            message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
    }

    // Without offered tools the run has a single final step, which still receives the hint.
    [Fact]
    public async Task Expression_hint_still_reaches_the_single_final_step_when_no_tools_are_offered()
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("分享一点。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            useNative: true, composer: new PromptComposer(null, null, ExpressionMode.Full));

        Assert.Equal(ConversationSendStatus.Completed,
            (await host.Orchestrator.SendAsync(role, "今天好累呀", default, new(ProjectId))).Status);

        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.ModelMessages,
            message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
    }

    // The frozen v0.4.1 fixtures are all tool-free single-turn chats. Their Expression behavior
    // must be unchanged: the hint still reaches the only (final) step of each send.
    [Theory]
    [InlineData("中午好，周末放假了，好开心")]
    [InlineData("本来还想收拾房间的，现在只想瘫着")]
    [InlineData("确实")]
    [InlineData("哈哈是这样")]
    [InlineData("今天好累呀")]
    [InlineData("先歇会儿吧")]
    [InlineData("帮我写一段简短说明")]
    [InlineData("来聊聊天吧")]
    public async Task Tool_free_chat_still_receives_the_expression_hint(string user)
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("合成回复。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            useNative: true, composer: new PromptComposer(null, null, ExpressionMode.Full));

        Assert.Equal(ConversationSendStatus.Completed,
            (await host.Orchestrator.SendAsync(role, user, default, new(ProjectId))).Status);

        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.ModelMessages,
            message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
    }
    // A tool-using chat turn: the model reads evidence, then answers. Expression must appear only
    // on the answer step, so the tool step is judged by tool-selection guidance alone.
    [Fact]
    public async Task Tool_using_chat_keeps_expression_out_of_the_evidence_step()
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(
            ProviderTurn.Call("fixture.read", "read-1", "{}"),
            ProviderTurn.Final("读到的内容是这样。"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            useNative: true, composer: new PromptComposer(null, null, ExpressionMode.Full));

        Assert.Equal(ConversationSendStatus.Completed,
            (await host.Orchestrator.SendAsync(role, "帮我看看这个错误", default, new(ProjectId))).Status);

        // Tool steps carry tools and must not carry Expression; the final answer step must.
        Assert.True(provider.Requests.Count >= 2);
        for (var index = 0; index < provider.Requests.Count; index++)
        {
            var request = provider.Requests[index];
            var hasHint = request.ModelMessages.Any(
                message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            var isFinal = request.Tools is null;
            Assert.True(isFinal || !hasHint,
                $"request {index}: tools={(request.Tools is null ? "none" : request.Tools.Count.ToString())} hint={hasHint}");
        }
        Assert.Contains(provider.Requests[^1].ModelMessages,
            message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
    }

    // A task turn that needs no tool still receives the hint on its single step.
    [Fact]
    public async Task Task_turn_without_tools_still_receives_expression()
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("简短说明如下。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            useNative: true, composer: new PromptComposer(null, null, ExpressionMode.Full));

        Assert.Equal(ConversationSendStatus.Completed,
            (await host.Orchestrator.SendAsync(role, "帮我写一段简短说明", default, new(ProjectId))).Status);

        var request = Assert.Single(provider.Requests);
        Assert.Contains(request.ModelMessages,
            message => message.Content.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
    }
}
