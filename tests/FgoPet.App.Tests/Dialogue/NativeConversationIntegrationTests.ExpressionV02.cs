using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_chat_intent_reaches_the_existing_replyer_without_an_extra_generation(bool native)
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("我有个想聊的小问题。"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer, settings: TestSettingsStore.Create(32768),
            contentResolver: new BoundPersonaResolver(binding), useNative: native, composer: new PromptComposer(null, null, ExpressionMode.Full));
        var result = await host.Orchestrator.SendAsync(role, "来聊聊天吧", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var request = Assert.Single(provider.Requests);
        var texts = native ? request.ModelMessages.Select(m => m.Content) : request.Messages.Select(m => m.Text);
        var hint = Assert.Single(texts, s => s.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
        Assert.Contains("optional_topic", hint);
        Assert.Contains("Response Intent / Open Conversation", hint);
        Assert.Equal("我有个想聊的小问题。", Assert.Single(observer.Turns).AssistantText);
    }

    [Theory]
    [InlineData(false, ExpressionMode.BasePolicy)]
    [InlineData(true, ExpressionMode.BasePolicy)]
    [InlineData(false, ExpressionMode.Full)]
    [InlineData(true, ExpressionMode.Full)]
    public async Task Expression_v02_is_one_ephemeral_hint_with_no_additional_model_call(bool native, ExpressionMode mode)
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("没有浏览器通道，尚未执行。"), ProviderTurn.Final("只是听你分享。"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer, settings: TestSettingsStore.Create(32768),
            contentResolver: new BoundPersonaResolver(binding), useNative: native, composer: new PromptComposer(null, null, mode));
        foreach (var user in new[] { "没有浏览器能力，任务还未执行。", "只是分享，不用给建议。" })
            Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(role, user, default, new(ProjectId))).Status);
        Assert.Equal(2, provider.Requests.Count);
        foreach (var request in provider.Requests)
        {
            var texts = native ? request.ModelMessages.Select(m => m.Content) : request.Messages.Select(m => m.Text);
            Assert.Single(texts, s => s.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            Assert.Contains(texts, s => s.Contains("不能换意思", StringComparison.Ordinal));
        }
        Assert.Equal(new[] { "没有浏览器通道，尚未执行。", "只是听你分享。" }, observer.Turns.Select(t => t.AssistantText));
        Assert.DoesNotContain(host.Conversations.LoadMessages(observer.Turns.Last().Scope.ConversationId, role),
            m => m.Text.Contains("Expression Layer", StringComparison.Ordinal));
    }
}
