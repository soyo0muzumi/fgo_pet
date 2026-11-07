using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Desktop_sticker_policy_is_not_inherited_by_text_only_independent_sessions()
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("收到。"), ProviderTurn.Final("收到。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            composer: new PromptComposer(null, null, ExpressionMode.Off, enableLocalStickers: true));
        var independent = host.Orchestrator.CreateIndependentSession("TextOnlyFixture", _ => true);
        try
        {
            Assert.Equal(ConversationSendStatus.Completed, (await independent.SendAsync(role, "你好", default, new(ProjectId))).Status);
            Assert.DoesNotContain(provider.Requests[0].ModelMessages, m => m.Content.StartsWith("可选本地图文表情：", StringComparison.Ordinal));
            Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(role, "你好", default, new(ProjectId))).Status);
            Assert.Contains(provider.Requests[1].ModelMessages, m => m.Content.StartsWith("可选本地图文表情：", StringComparison.Ordinal));
        }
        finally { independent.CloseIndependentSession(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sticker_nomination_uses_one_final_call_and_completion_matches_persisted_presentation(bool native)
    {
        const string role = "mash_kyrielight";
        const string raw = """{"text":"做到了呢。","emotion":"happy","sticker":{"assetId":"mash.happy","intent":"happy","utility":"enhance","salience":"high","context":"social","position":"after"}}""";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final(raw));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding),
            useNative: native, composer: new PromptComposer(null, null, ExpressionMode.Off, enableLocalStickers: true));
        var updates = new List<ConversationUpdate>();
        host.Orchestrator.Updated += updates.Add;
        var result = await host.Orchestrator.SendAsync(role, "终于完成了！", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var request = Assert.Single(provider.Requests);
        if (native) Assert.Contains(request.ModelMessages, message => message.Content.StartsWith("可选本地图文表情：", StringComparison.Ordinal));
        else Assert.Contains(request.Messages, message => message.Text.StartsWith("可选本地图文表情：", StringComparison.Ordinal));
        var reply = host.Conversations.LoadMessages(result.ConversationId, role).Last();
        var completed = Assert.Single(updates.Where(update => update.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal("做到了呢。", reply.Text);
        Assert.Equal(reply.Sticker, completed.Sticker);
        Assert.DoesNotContain("sticker", reply.Text);
    }
}
