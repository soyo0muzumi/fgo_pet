using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ExpressionStaticPolicyTests
{
    private static readonly ModelRouteKey Route = new("test", "endpoint", "model", "1");
    private static readonly PromptBudget Budget = PromptBudget.Resolve(
        new(Route, 32768, null, ContextLimitSource.Override, "test"), 2048);

    private static PromptContext Context(string user, IReadOnlyList<PromptMessage>? messages = null,
        IReadOnlyList<ConversationPromptBlock>? contributions = null) => new(
        new("mash_kyrielight", "official.mash", "1", "casual", "1", "1"),
        new PersonaBundle("mash_kyrielight", "official.mash", "1", "1", "synthetic persona", []),
        [], contributions ?? [], "", messages ?? [], user);

    [Theory]
    [InlineData("今天好累呀")]
    [InlineData("确实")]
    [InlineData("分析这个架构")]
    [InlineData("来聊聊天吧")]
    public void Active_modes_share_one_static_policy_without_automatic_control(string user)
    {
        var context = Context(user, [new(ChatMessageRole.User, "分享一下今天的事"),
            new(ChatMessageRole.Assistant, "synthetic previous reply")]);
        var baseline = MashExpressionV02.Build(Context("你好"), false);
        foreach (var mode in new[] { ExpressionMode.BasePolicy, ExpressionMode.Full })
        {
            var prompt = new PromptComposer(null, null, mode).Compose(context, Route, Budget);
            var hint = Assert.Single(prompt.Messages,
                message => message.Text.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            Assert.Equal(baseline, hint.Text);
            foreach (var removed in new[] { "ResponseIntent:", "Situational Patterns", "ConversationControl:",
                "follow_up=", "QuestionPolicy", "三句里至少有一句在十个字以内" })
                Assert.DoesNotContain(removed, hint.Text);
            Assert.Contains("不能换意思", hint.Text);
            Assert.Contains("先带出一个具体话题", hint.Text);
        }
    }

    [Fact]
    public void Explicit_host_constraints_survive_without_an_inferred_intent_or_json_control()
    {
        var intent = new ResponseIntent("answer", "uncertain", "current_topic", "reserved", "brief", "none",
            ["advice", "capability_claim"]);
        var context = Context("这个结论确定了吗？") with { ResponseIntent = intent };
        foreach (var mode in new[] { ExpressionMode.BasePolicy, ExpressionMode.Full })
        {
            var prompt = new PromptComposer(null, null, mode).Compose(context, Route, Budget);
            var hint = Assert.Single(prompt.Messages,
                message => message.Text.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
            Assert.Contains("stance=uncertain", hint.Text);
            Assert.Contains("avoid=advice,capability_claim", hint.Text);
            Assert.DoesNotContain("ResponseIntent:", hint.Text);
            Assert.DoesNotContain("ConversationControl:", hint.Text);
        }
        Assert.Same(intent, context.ResponseIntent);
    }

    [Theory]
    [InlineData("来聊聊天吧", true)]
    [InlineData("今天好累呀", true)]
    [InlineData("先歇会儿吧", false)]
    [InlineData("分析这个架构", false)]
    public void Removing_expression_control_preserves_material_admission(string user, bool expected)
    {
        var context = Context(user, contributions:
            [new ConversationPromptBlock("topic-material", "synthetic external context", Whole: true)]);
        foreach (var mode in new[] { ExpressionMode.Off, ExpressionMode.BasePolicy, ExpressionMode.Full })
        {
            var prompt = new PromptComposer(null, null, mode).Compose(context, Route, Budget);
            Assert.Equal(expected, prompt.Messages.Any(
                message => message.Text.Contains("source=\"topic-material\"", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Production_expression_has_no_mutable_experiment_hook()
        => Assert.Null(typeof(PromptComposer).Assembly.GetType("FgoPet.App.Dialogue.MashExpressionV02")!
            .GetProperty("CustomBuildHook", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
}
