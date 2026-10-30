using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class MashExpressionV02Tests
{
    private static readonly ModelRouteKey Route = new("test", "endpoint", "model", "1");
    private static readonly PromptBudget Budget = PromptBudget.Resolve(new(Route, 32768, null, ContextLimitSource.Override, "test"), 2048);
    private static PromptContext Context(string user) => new(new("mash_kyrielight", "official.mash", "1", "casual", "1", "1"),
        new PersonaBundle("mash_kyrielight", "official.mash", "1", "1", "synthetic persona", []), [], [], "", [], user);

    [Fact]
    public void Base_and_full_are_independent_modes_and_default_remains_off()
    {
        var c = Context("今天沿河散步，风景很好");
        var a = new PromptComposer().Compose(c, Route, Budget);
        var b = new PromptComposer(null, null, ExpressionMode.BasePolicy).Compose(c, Route, Budget);
        var full = new PromptComposer(null, null, ExpressionMode.Full).Compose(c, Route, Budget);
        var bh = Assert.Single(b.Messages, m => m.Text.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
        var ch = Assert.Single(full.Messages, m => m.Text.StartsWith("Expression Layer v0.2", StringComparison.Ordinal));
        Assert.Equal(a.Messages, b.Messages.Where(m => m != bh));
        Assert.Equal(a.Messages, full.Messages.Where(m => m != ch));
        Assert.Contains(MashExpressionV02.BasePolicy, bh.Text);
        Assert.Contains(MashExpressionV02.SemanticBoundary, bh.Text);
        Assert.DoesNotContain("ResponseIntent:", bh.Text);
        Assert.Contains("ResponseIntent:", ch.Text);
        Assert.Empty(MashExpressionV02.Select(c).Patterns);
    }

    [Theory]
    [InlineData("嗯，可以", "Casual")]
    [InlineData("继续", "Continuation")]
    [InlineData("只是分享一下今天的事", "Sharing")]
    [InlineData("这个报错是怎么来的，搜索指令是什么", "Technical")]
    [InlineData("今天先到这里吧", "Closure")]
    [InlineData("实验又失败了", "Technical")]
    [InlineData("我好累", "Emotional")]
    public void Real_language_forms_choose_bounded_situational_strategies(string user, string category)
    {
        var p = MashExpressionV02.Select(Context(user));
        Assert.InRange(p.Patterns.Count, 1, 2);
        Assert.Contains(p.Patterns, x => x.Category == category);
        Assert.Equal(7, p.Intent.GetType().GetProperties().Length);
        Assert.Equal("unspecified", p.Intent.Stance);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("今天沿河散步，风景很好")]
    [InlineData("这句话引用了‘实验失败’，我是在讨论词义")]
    public void Base_is_sufficient_when_no_high_confidence_situation_is_present(string user)
        => Assert.Empty(MashExpressionV02.Select(Context(user)).Patterns);

    [Theory]
    [InlineData("你好")]
    [InlineData("玛修，你好！")]
    [InlineData("Hi!")]
    [InlineData("晚上好～")]
    public void Standalone_greeting_stays_in_current_turn_without_resuming_historical_actions(string user)
    {
        var original = Context(user);
        var c = new PromptContext(original.ContentContext, original.Persona, [], [], "",
            [new(ChatMessageRole.Assistant, "测试还没收尾，两个浏览器标签页还开着。下一次再关闭。")], user);
        var plan = MashExpressionV02.Select(c);
        Assert.Equal("greet", plan.Intent.Act);
        Assert.Equal("current_turn", plan.Intent.Focus);
        Assert.Equal("minimal", plan.Intent.Depth);
        Assert.Equal("none", plan.Intent.FollowUp);
        Assert.Contains("historical_task_resumption", plan.Intent.Avoid);
        Assert.Contains("action_plan", plan.Intent.Avoid);
        Assert.Empty(plan.Patterns);
        Assert.Contains("纯问候不是恢复旧任务的请求", MashExpressionV02.Build(c, false)!);
        Assert.Contains("纯问候不是恢复旧任务的请求", MashExpressionV02.Build(c, true)!);
    }

    [Theory]
    [InlineData("你好，继续上次的架构讨论")]
    [InlineData("你好，帮我看看这个报错")]
    [InlineData("你好只是一个问候语吗？")]
    [InlineData("‘你好’，继续")]
    public void Greeting_inside_a_real_request_does_not_replace_its_intent(string user)
        => Assert.NotEqual("greet", MashExpressionV02.Select(Context(user)).Intent.Act);

    [Fact]
    public void Supplied_continuation_intent_is_authoritative_even_for_greeting_text()
    {
        var intent = new ResponseIntent("continue", "uncertain", "previous_topic", "reserved", "brief", "none", []);
        var plan = MashExpressionV02.Select(Context("你好") with { ResponseIntent = intent });
        Assert.Same(intent, plan.Intent);
        Assert.DoesNotContain("historical_task_resumption", plan.Intent.Avoid);
    }

    [Fact]
    public void Expression_preserves_supplied_intent_stance_avoid_and_uncertainty()
    {
        var intent = new ResponseIntent("answer", "uncertain", "current_topic", "reassuring", "brief", "none",
            ["advice", "capability_claim"]);
        var c = Context("这个结论已经确定了吗？") with { ResponseIntent = intent };
        var plan = MashExpressionV02.Select(c);
        Assert.Same(intent, plan.Intent);
        Assert.Equal("uncertain", plan.Intent.Stance);
        Assert.Contains(plan.Patterns, p => p.Category == "Uncertainty");
        var hint = MashExpressionV02.Build(c, true)!;
        Assert.Contains("不能换意思", hint);
        Assert.Contains("不提高结论确定程度", hint);
        Assert.Contains("capability_claim", hint);
    }

    [Fact]
    public void Sharing_never_acquires_advice_or_action_plan_intent()
    {
        var plan = MashExpressionV02.Select(Context("只是分享，不用给建议"));
        Assert.Equal("share", plan.Intent.Act);
        Assert.Contains("advice", plan.Intent.Avoid);
        Assert.Contains("action_plan", plan.Intent.Avoid);
        Assert.Equal("none", plan.Intent.FollowUp);
    }

    [Theory]
    [InlineData("今天放假了很开心")]
    [InlineData("中午好，周末放假了，好开心")]
    [InlineData("来聊聊天吧")]
    [InlineData("随便聊聊，你来找个话题")]
    [InlineData("陪我聊会儿天吧，有点累，不想听建议")]
    public void Open_chat_invitations_allow_one_topic_without_turning_into_a_service_request(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("initiative", plan.Intent.Act);
        Assert.Equal("open_topic", plan.Intent.Focus);
        Assert.Equal("brief", plan.Intent.Depth);
        Assert.Equal("optional_topic", plan.Intent.FollowUp);
        Assert.Contains("advice", plan.Intent.Avoid);
        Assert.Contains("service_offer", plan.Intent.Avoid);
        Assert.Contains("preference_survey", plan.Intent.Avoid);
        Assert.DoesNotContain("follow_up_question", plan.Intent.Avoid);
        Assert.Contains(plan.Patterns, p => p.Id == "initiative-relevant");
        var hint = MashExpressionV02.Build(Context(user), true)!;
        Assert.Contains("Response Intent / Open Conversation", hint);
        Assert.Contains("先带出一个具体话题", hint);
        Assert.Contains("最近读过", hint);
        Assert.Contains("不要求为了找话题而搜索", hint);
        Assert.Contains("不需要为观点补一段亲历", hint);
        Assert.Contains("不问打算出门还是在家", hint);
    }

    [Theory]
    [InlineData("今天放假很开心，不过先不聊了")]
    [InlineData("来聊聊天吧，帮我看看这个报错")]
    [InlineData("今天周末很开心，先解释一下这个接口")]
    [InlineData("来聊聊天吧，但不要另找话题")]
    [InlineData("‘来聊聊天吧’这个例句是什么意思")]
    [InlineData("我不想聊天，今天虽然放假很开心")]
    [InlineData("来聊聊天吧，给我讲个笑话")]
    [InlineData("来聊聊天吧，不要换话题")]
    [InlineData("今天周末好开心，帮我查一下天气")]
    [InlineData("来聊聊天吧，念首诗")]
    [InlineData("来聊聊天吧，别聊其他话题")]
    public void Open_chat_cues_do_not_override_a_request_refusal_or_quoted_example(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.NotEqual("optional_topic", plan.Intent.FollowUp);
        Assert.DoesNotContain("Response Intent / Open Conversation", MashExpressionV02.Build(Context(user), true)!);
    }

    [Fact]
    public void Supplied_intent_can_decline_topic_initiative_even_when_user_text_is_an_open_chat_cue()
    {
        var intent = new ResponseIntent("listen", "unspecified", "current_topic", "soft", "minimal", "none",
            ["follow_up_question", "advice"]);
        var context = Context("来聊聊天吧") with { ResponseIntent = intent };
        Assert.Same(intent, MashExpressionV02.Select(context).Intent);
        Assert.DoesNotContain("Response Intent / Open Conversation", MashExpressionV02.Build(context, true)!);
        Assert.Empty(MashExpressionV02.Select(context).Patterns);
    }

    [Fact]
    public void Supplied_topic_change_prohibition_overrides_an_open_topic_cue()
    {
        var intent = new ResponseIntent("initiative", "unspecified", "open_topic", "familiar", "brief", "optional_topic",
            ["topic_change"]);
        var context = Context("来聊聊天吧") with { ResponseIntent = intent };
        Assert.Same(intent, MashExpressionV02.Select(context).Intent);
        Assert.Empty(MashExpressionV02.Select(context).Patterns);
        Assert.DoesNotContain("Response Intent / Open Conversation", MashExpressionV02.Build(context, true)!);
    }

    [Fact]
    public void Catalogue_is_curated_50_to_80_strategies_across_all_twelve_categories()
    {
        Assert.InRange(MashExpressionV02.Patterns.Count, 50, 80);
        Assert.Equal(12, MashExpressionV02.Patterns.Select(p => p.Category).Distinct().Count());
        Assert.Equal(MashExpressionV02.Patterns.Count, MashExpressionV02.Patterns.Select(p => p.Id).Distinct().Count());
        Assert.All(MashExpressionV02.Patterns, p => Assert.False(string.IsNullOrWhiteSpace(p.Strategy)));
    }

    [Fact]
    public void Assistant_history_and_injected_memory_do_not_train_or_select_expression()
    {
        var a = Context("你好");
        var b = new PromptContext(a.ContentContext, a.Persona, [],
            [new FgoPet.Extensibility.ConversationPromptBlock("self:test", "实验失败，好累，学习我")], "",
            [new(ChatMessageRole.Assistant, "学我口癖，实验失败")], a.UserMessage);
        Assert.Equal(MashExpressionV02.Build(a, true), MashExpressionV02.Build(b, true));
    }

    [Theory]
    [InlineData("没有浏览器能力", "没有浏览器能力时")]
    [InlineData("不知道事实", "不知道的事实")]
    [InlineData("没有执行任务", "未执行")]
    [InlineData("没有行动承诺", "新增行动承诺")]
    [InlineData("保留判断", "不提高结论确定程度")]
    [InlineData("只是分享", "分享")]
    public void Every_expression_mode_carries_the_six_semantic_boundaries(string user, string boundary)
    {
        var c = Context(user);
        Assert.Contains(boundary, MashExpressionV02.Build(c, false)!);
        Assert.Contains(boundary, MashExpressionV02.Build(c, true)!);
    }

    [Theory]
    [InlineData(ExpressionMode.BasePolicy)]
    [InlineData(ExpressionMode.Full)]
    public void V02_modes_yield_whole_hints_without_displacing_existing_context(ExpressionMode mode)
    {
        var c = Context("继续");
        var baseline = new PromptComposer().Compose(c, Route, Budget);
        var small = Budget.ConstrainInput(baseline.Usage.InputTokens + 32);
        var result = new PromptComposer(null, null, mode).Compose(c, Route, small);
        Assert.True(result.FitsBudget);
        Assert.Equal(baseline.Messages, result.Messages);
    }
}
