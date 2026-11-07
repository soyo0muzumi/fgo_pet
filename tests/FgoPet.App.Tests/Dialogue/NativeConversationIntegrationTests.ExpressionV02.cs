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
    public async Task Static_expression_reaches_replyer_without_duplicate_history(bool native)
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("分享一下另一件事。"), ProviderTurn.Final("继续接住处境。"), ProviderTurn.Final("再接一小步。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), settings: TestSettingsStore.Create(32768),
            contentResolver: new BoundPersonaResolver(binding), useNative: native, composer: new PromptComposer(null, null, ExpressionMode.Full));
        foreach (var user in new[] { "今天好累呀", "接连几件事情挤在一起了", "有一点" })
            Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(role, user, default, new(ProjectId))).Status);
        Assert.Equal(3, provider.Requests.Count);
        for (var index = 0; index < provider.Requests.Count; index++)
        {
            var request = provider.Requests[index];
            var texts = native ? request.ModelMessages.Select(m => m.Content) : request.Messages.Select(m => m.Text);
            var hint = Assert.Single(texts, s => s.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            Assert.Equal(MashExpressionV02.Build(new PromptContext(key, binding.Persona!, [], [], "", [], "你好"), false), hint);
            Assert.DoesNotContain("ResponseIntent:", hint);
            Assert.DoesNotContain("Situational Patterns", hint);
            Assert.DoesNotContain("ConversationControl:", hint);
            var userCount = native ? request.ModelMessages.Count(m => m.Role == FgoPet.Kernel.Agent.ModelMessageRole.User)
                : request.Messages.Count(m => m.Role == ChatMessageRole.User);
            Assert.Equal(index + 1, userCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Everyday_continuation_and_backchannels_reach_each_existing_reply_request(bool native)
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("先接住具体话题。"), ProviderTurn.Final("再贡献一点细节。"), ProviderTurn.Final("给你留一点空间。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), settings: TestSettingsStore.Create(32768),
            contentResolver: new BoundPersonaResolver(binding), useNative: native, composer: new PromptComposer(null, null, ExpressionMode.Full));
        var users = new[] { "又是类魂，精力不够打不了这类游戏", "那倒也是", "只是分享，不用给建议" };
        foreach (var user in users)
            Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(role, user, default, new(ProjectId))).Status);
        Assert.Equal(3, provider.Requests.Count);
        for (var i = 0; i < provider.Requests.Count; i++)
        {
            var request = provider.Requests[i];
            var texts = native ? request.ModelMessages.Select(m => m.Content) : request.Messages.Select(m => m.Text);
            var hint = Assert.Single(texts, s => s.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            Assert.Contains(MashExpressionV02.BasePolicy, hint);
            Assert.DoesNotContain("ResponseIntent:", hint);
            Assert.DoesNotContain("Situational Patterns", hint);
            Assert.DoesNotContain("ConversationControl:", hint);
        }
    }

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
        Assert.Contains("先带出一个具体话题", hint);
        Assert.DoesNotContain("Response Intent / Open Conversation", hint);
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
