using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expression_opt_in_preserves_two_turn_source_persona_success_with_small_context(bool native)
    {
        var binding = ResolveSourcePersona();
        var provider = new ScriptedProvider(PersonaFinal("合成答复一"), PersonaFinal("合成答复二"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(FgoPet.Extensibility.ToolEffect.ReadOnly),
            new RecordingObserver(), contentResolver: new BoundPersonaResolver(binding), useNative: native,
            composer: new PromptComposer(null, null, true));
        foreach (var input in new[] { "请帮我整理当前问题，不执行任何操作。", "继续上一个问题，保留限定。" })
            Assert.Equal(ConversationSendStatus.Completed,
                (await host.Orchestrator.SendAsync(SourcePersonaRole, input, default, new(ProjectId))).Status);
        Assert.Equal(native ? 4 : 2, provider.Requests.Count);
        var route = ModelRouteKey.From(host.Settings.Load().ModelConnection!);
        var budget = PromptBudget.Resolve(new(route, 8192, null, ContextLimitSource.Override, "test"), 512);
        var meter = new RequestTokenMeter();
        var persona = PromptInjectionGuard.WrapPersona("servant_core", binding.Persona!.CoreText);
        foreach (var request in provider.Requests)
        {
            Assert.InRange(meter.Measure(route, request).InputTokens, 1, budget.InputTokens);
            var texts = native ? request.ModelMessages.Select(m => m.Content) : request.Messages.Select(m => m.Text);
            Assert.Contains(persona, texts);
        }
        // Omission itself is covered with an explicitly constrained budget in composer tests;
        // a source Persona that fits must not be required to drop the optional hint.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expression_is_ephemeral_across_turns_and_observers_receive_only_final_reply(bool native)
    {
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []),
            [], [], "synthetic-persona", "synthetic-knowledge");
        var provider = new ScriptedProvider(ProviderTurn.Final("第一轮正文"), ProviderTurn.Final("第二轮正文"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, null, observer,
            contentResolver: new BoundPersonaResolver(binding), useNative: native,
            composer: new PromptComposer(null, null, true));
        foreach (var input in new[] { "今天有点累", "这个接口为什么失败？" })
        {
            var result = await host.Orchestrator.SendAsync(role, input, default, new(ProjectId));
            Assert.Equal(ConversationSendStatus.Completed, result.Status);
        }
        foreach (var request in provider.Requests)
        {
            var texts = native ? request.ModelMessages.Select(m => m.Content) : request.Messages.Select(m => m.Text);
            Assert.Single(texts, t => t.StartsWith("Expression Layer v0", StringComparison.Ordinal));
        }
        Assert.Equal(2, observer.Turns.Count);
        Assert.Equal(new[] { "第一轮正文", "第二轮正文" }, observer.Turns.Select(t => t.AssistantText));
        Assert.All(observer.Turns, t => {
            Assert.DoesNotContain("Expression Layer", t.AssistantText);
            Assert.DoesNotContain("reply_act=", t.AssistantText);
        });
        var last = observer.Turns.Last();
        var originals = host.Conversations.LoadMessages(last.Scope.ConversationId, role);
        Assert.Equal(4, originals.Count);
        Assert.DoesNotContain(originals, m => m.Text.Contains("Expression Layer", StringComparison.Ordinal));
        // Native may retain its existing tool-finalization control request; Expression adds none.
        Assert.InRange(provider.Requests.Count, 2, native ? 4 : 2);
    }
}
