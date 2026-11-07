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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Agency_allows_a_response_without_manufacturing_a_contribution(bool full)
    {
        // Static guidance permits a contribution without requiring a new point on every turn.
        var hint = MashExpressionV02.Build(Context("确实"), full)!;
        Assert.Contains("日常交谈也可贡献自己的观察、联想和判断", hint);
        Assert.Contains("也不强加新切面", hint);
        Assert.Contains("单点澄清关键缺口，不猜答", hint);
        Assert.Contains("不要使用“需要我帮你……吗”", hint);
        Assert.DoesNotContain("需要我帮你查吗", hint);
    }

    private static PromptContext WithHistory(string user, params PromptMessage[] history)
    {
        var context = Context(user);
        return new(context.ContentContext, context.Persona, [], [], "", history, user);
    }

    [Theory]
    [InlineData("算了，我去睡了。")]
    [InlineData("我先去睡觉了")]
    [InlineData("先睡了")]
    public void Explicit_sleep_departure_uses_existing_rest_closure(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("close", plan.Intent.Act);
        Assert.Contains(plan.Patterns, pattern => pattern.Id == "close-rest");
    }

    [Theory]
    [InlineData("“算了，我去睡了。”这句话怎么翻译")]
    [InlineData("最近在讨论睡觉和记忆的关系")]
    public void Quoted_sleep_departures_and_sleep_discussion_are_not_closure(string user)
        => Assert.NotEqual("close", MashExpressionV02.Select(Context(user)).Intent.Act);

    [Theory]
    [InlineData("今天把柜子整理好了", "share-receive")]
    [InlineData("那件事已经做完了", "share-receive")]
    [InlineData("今天去了一趟旧书店", "share-detail")]
    [InlineData("昨天路上遇见了一位老朋友", "share-detail")]
    [InlineData("现在已经搬到新住处了", "share-detail")]
    [InlineData("有个好消息，我拿到了录取通知", "share-good-news")]
    [InlineData("终于把门铃修好了！", "share-good-news")]
    [InlineData("今天完成了收纳，好开心", "share-good-news")]
    [InlineData("分享一下今天的小事", "share-no-solution")]
    public void Generic_completed_events_experiences_updates_and_good_news_reach_sharing_patterns(string user, string pattern)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("share", plan.Intent.Act);
        Assert.Equal("familiar", plan.Intent.Tone);
        Assert.Equal("continue_thread", plan.Intent.FollowUp);
        Assert.Equal(new[] { pattern }, plan.Patterns.Select(p => p.Id));
        Assert.Contains("advice", plan.Intent.Avoid);
        Assert.Contains("service_offer", plan.Intent.Avoid);
        Assert.DoesNotContain("follow_up_question", plan.Intent.Avoid);
    }

    [Theory]
    [InlineData("今天把柜子整理好了，帮我写一段说明")]
    [InlineData("好消息，今天代码编译通过了")]
    [InlineData("今天把接口实现好了")]
    [InlineData("今天完成了分析")]
    [InlineData("今天终于收尾了，好累")]
    [InlineData("刚刚完成了，可我有点失望")]
    [InlineData("今天把柜子整理好了，接下来怎么办")]
    [InlineData("今天把柜子整理好了没？")]
    [InlineData("今天去过哪里了")]
    [InlineData("“今天把柜子整理好了”是一个例句")]
    [InlineData("如果今天把柜子整理好了，就能歇会儿")]
    [InlineData("明天要是把柜子整理好了，就轻松了")]
    [InlineData("好消息还没有发生，只是我的打算")]
    public void Strong_current_signals_questions_and_examples_do_not_become_inferred_sharing(string user)
        => Assert.NotEqual("share", MashExpressionV02.Select(Context(user)).Intent.Act);

    [Theory]
    [InlineData("有个好消息，我拿到了录取通知", "share-good-news")]
    [InlineData("今天去了一趟旧书店", "share-detail")]
    public void Newly_reachable_sharing_frames_are_inherited_without_assistant_input(string previous, string pattern)
    {
        var plan = MashExpressionV02.Select(WithHistory("确实", new PromptMessage(ChatMessageRole.User, previous),
            new PromptMessage(ChatMessageRole.Assistant, "今天好累呀")));
        Assert.Equal("familiar", plan.Intent.Tone);
        Assert.Equal(new[] { "continue-small-step", pattern }, plan.Patterns.Select(p => p.Id));
    }

    [Fact]
    public void Every_sharing_pattern_has_a_current_user_route_without_supplied_mind_intent()
    {
        var messages = new[] { "今天把柜子整理好了", "今天去了一趟旧书店", "只是分享，不用给建议", "有个好消息，我拿到了录取通知" };
        var reachable = messages.SelectMany(user => MashExpressionV02.Select(Context(user)).Patterns).Select(p => p.Id).ToHashSet();
        Assert.All(MashExpressionV02.Patterns.Where(p => p.Category == "Sharing"), pattern => Assert.Contains(pattern.Id, reachable));
    }

    [Theory]
    [InlineData("今天好累呀", "emotion-tired")]
    [InlineData("只是吐槽，真烦", "emotion-vent")]
    [InlineData("我有点失望", "emotion-disappointment")]
    [InlineData("我是不是不行", "emotion-self-doubt")]
    public void Unclassified_user_followup_inherits_recent_listening_frame(string previous, string pattern)
    {
        var plan = MashExpressionV02.Select(WithHistory("接连几件事情挤在一起了", new PromptMessage(ChatMessageRole.User, previous)));
        Assert.Equal("listen", plan.Intent.Act);
        Assert.Equal("soft", plan.Intent.Tone);
        Assert.Equal("continue_thread", plan.Intent.FollowUp);
        Assert.Equal(new[] { pattern }, plan.Patterns.Select(p => p.Id));
    }

    [Theory]
    [InlineData("有一点")]
    [InlineData("是啊")]
    [InlineData("确实")]
    public void Weak_backchannel_retains_listening_tone_and_secondary_pattern(string user)
    {
        var plan = MashExpressionV02.Select(WithHistory(user, new PromptMessage(ChatMessageRole.User, "今天好累呀")));
        Assert.Equal("soft", plan.Intent.Tone);
        Assert.Equal(new[] { "continue-small-step", "emotion-tired" }, plan.Patterns.Select(p => p.Id));
    }

    [Theory]
    [InlineData("确实挺耗神的")]
    [InlineData("主要是这回节奏慢了一点")]
    [InlineData("就是每次都重复一遍")]
    [InlineData("主要是太累了")]
    [InlineData("就是有点累")]
    public void Sharing_followup_retains_familiar_frame(string user)
    {
        var plan = MashExpressionV02.Select(WithHistory(user, new PromptMessage(ChatMessageRole.User, "这件事精力不够")));
        Assert.Equal("familiar", plan.Intent.Tone);
        Assert.Contains(plan.Patterns, p => p.Id == "share-receive");
        Assert.DoesNotContain(plan.Patterns, p => p.Id == "emotion-tired");
    }

    [Fact]
    public void Unclassified_followup_inherits_share_primary()
    {
        var plan = MashExpressionV02.Select(WithHistory("这回比之前多了一层变化", new PromptMessage(ChatMessageRole.User, "分享一下今天的小事")));
        Assert.Equal("share", plan.Intent.Act);
        Assert.Equal("familiar", plan.Intent.Tone);
        Assert.Equal(new[] { "share-no-solution" }, plan.Patterns.Select(p => p.Id));
    }

    [Theory]
    [InlineData("是啊，不过现在真的有点撑不住了")]
    [InlineData("主要是我现在真的好累")]
    [InlineData("确实，不过我有点失望")]
    [InlineData("其实真的好累")]
    public void Explicit_new_emotion_overrides_previous_sharing(string user)
    {
        var plan = MashExpressionV02.Select(WithHistory(user, new PromptMessage(ChatMessageRole.User, "分享一下今天的小事")));
        Assert.Equal("listen", plan.Intent.Act);
        Assert.Equal("soft", plan.Intent.Tone);
        Assert.Contains(plan.Patterns, p => p.Category == "Emotional");
        Assert.DoesNotContain(plan.Patterns, p => p.Category == "Sharing");
    }

    [Theory]
    [InlineData("嗯，对了帮我看看这个错误")]
    [InlineData("是啊，不过这段代码报错了")]
    [InlineData("主要是接口需要怎么实现")]
    [InlineData("就是不想分析了")]
    public void Explicit_task_cuts_off_inherited_listening(string user)
    {
        var plan = MashExpressionV02.Select(WithHistory(user, new PromptMessage(ChatMessageRole.User, "今天好累呀")));
        Assert.Equal("none", plan.Intent.FollowUp);
        Assert.DoesNotContain(plan.Patterns, p => p.Category == "Emotional");
    }

    [Theory]
    [InlineData("今天先到这里")]
    [InlineData("你好")]
    [InlineData("帮我看看这个错误")]
    [InlineData("只是分享，不用给建议")]
    [InlineData("嗯")]
    public void User_boundary_stops_history_lookback(string boundary)
    {
        var plan = MashExpressionV02.Select(WithHistory("这回比之前多了一层变化",
            new PromptMessage(ChatMessageRole.User, "今天好累呀"), new PromptMessage(ChatMessageRole.User, boundary)));
        Assert.Equal("familiar", plan.Intent.Tone);
        Assert.DoesNotContain(plan.Patterns, p => p.Category == "Emotional");
    }

    [Fact]
    public void History_lookup_is_bounded_to_two_user_messages_and_ignores_assistant_text()
    {
        var plan = MashExpressionV02.Select(WithHistory("这回比之前多了一层变化",
            new PromptMessage(ChatMessageRole.User, "今天好累呀"), new PromptMessage(ChatMessageRole.User, "第一处有一点差别"),
            new PromptMessage(ChatMessageRole.User, "第二处也有一点差别"), new PromptMessage(ChatMessageRole.Assistant, "今天好累呀")));
        Assert.Equal("familiar", plan.Intent.Tone);
        Assert.Empty(plan.Patterns);
        var nearby = MashExpressionV02.Select(WithHistory("有一点", new PromptMessage(ChatMessageRole.User, "今天好累呀"),
            new PromptMessage(ChatMessageRole.Assistant, "分享一下今天的小事"), new PromptMessage(ChatMessageRole.User, "接连几件事情挤在一起了")));
        Assert.Equal("soft", nearby.Intent.Tone);
        Assert.Equal(new[] { "continue-small-step", "emotion-tired" }, nearby.Patterns.Select(p => p.Id));
    }

    [Fact]
    public void Inherited_patterns_yield_whole_hint_without_displacing_history_under_tight_budget()
    {
        var context = WithHistory("有一点", new PromptMessage(ChatMessageRole.User, "今天好累呀"),
            new PromptMessage(ChatMessageRole.Assistant, "只是分享另一件事。"));
        var baseline = new PromptComposer().Compose(context, Route, Budget);
        var limited = Budget.ConstrainInput(baseline.Usage.InputTokens + 32);
        var result = new PromptComposer(null, null, ExpressionMode.Full).Compose(context, Route, limited);
        Assert.True(result.FitsBudget);
        Assert.Equal(baseline.Messages, result.Messages);
        Assert.Equal(new[] { "continue-small-step", "emotion-tired" }, MashExpressionV02.Select(context).Patterns.Select(p => p.Id));
    }

    [Fact]
    public void Base_and_full_share_static_policy_and_default_remains_off()
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
        Assert.Equal(bh.Text, ch.Text);
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
        Assert.DoesNotContain("Response Intent / Open Conversation", hint);
        Assert.Contains("先带出一个具体话题", hint);
        Assert.Contains("最近读过", hint);
        Assert.Contains("不要求为了找话题而搜索", hint);
        Assert.Contains("不编造最近读过什么或持续关注的经历", hint);
        Assert.Contains("不用“是 A 还是 B”二选一盘问", hint);
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

    [Theory]
    [InlineData("今天沿河散步，风景很好")]
    [InlineData("分享一下今天的事")]
    [InlineData("今天好累呀")]
    [InlineData("又是类魂，精力不够打不了这类游戏")]
    [InlineData("继续")]
    [InlineData("这是什么类型的游戏呀")]
    public void Everyday_turns_keep_conversation_open_without_service_or_survey(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("continue_thread", plan.Intent.FollowUp);
        Assert.DoesNotContain("follow_up_question", plan.Intent.Avoid);
        Assert.DoesNotContain(plan.Patterns, p => p.Id == "share-space");
        foreach (var item in new[] { "service_offer", "survey_question", "forced_choice", "advice", "action_plan", "reassurance", "over_explanation" })
            Assert.Contains(item, plan.Intent.Avoid);
    }

    [Theory]
    [InlineData("确实")]
    [InlineData("那倒也是")]
    [InlineData("有一点")]
    [InlineData("有一点吧")]
    [InlineData("哈哈是这样")]
    [InlineData("也是")]
    [InlineData("是啊")]
    [InlineData("没错")]
    [InlineData("主要是太累了")]
    [InlineData("确实挺费劲的")]
    public void Backchannels_contribute_a_small_step(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("continue_thread", plan.Intent.FollowUp);
        Assert.Contains(plan.Patterns, p => p.Id == "continue-small-step");
    }

    [Theory]
    [InlineData("只是分享，不用给建议")]
    [InlineData("陪我聊会儿天吧，有点累，不想听建议")]
    [InlineData("今天好累，别追问")]
    [InlineData("不想聊")]
    public void Explicit_space_overrides_continuation_and_open_topic(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("none", plan.Intent.FollowUp);
        Assert.Contains("follow_up_question", plan.Intent.Avoid);
        Assert.Contains(plan.Patterns, p => p.Id == "share-space");
        Assert.DoesNotContain("Response Intent / Open Conversation", MashExpressionV02.Build(Context(user), true)!);
    }

    [Theory]
    [InlineData("嗯")]
    [InlineData("好的")]
    [InlineData("嗯，可以")]
    [InlineData("明白了")]
    public void Task_acknowledgments_still_close_minimally(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("acknowledge", plan.Intent.Act);
        Assert.Equal("none", plan.Intent.FollowUp);
        Assert.Equal("minimal", plan.Intent.Depth);
    }

    [Theory]
    [InlineData("又是类魂，精力不够打不了这类游戏")]
    [InlineData("这个游戏玩不动了")]
    public void Taste_or_energy_comments_are_sharing_not_psychological_reassurance(string user)
    {
        var plan = MashExpressionV02.Select(Context(user));
        Assert.Equal("share", plan.Intent.Act);
        Assert.Contains(plan.Patterns, p => p.Id == "share-receive");
        Assert.DoesNotContain(plan.Patterns, p => p.Category == "Emotional");
    }

    [Theory]
    [InlineData("这个报错怎么修")]
    [InlineData("分析这个架构")]
    [InlineData("接口代码怎么实现")]
    [InlineData("帮我写一段说明")]
    [InlineData("打开浏览器")]
    [InlineData("实验又失败了")]
    [InlineData("这个游戏打不动，怎么办")]
    [InlineData("代码报错，游戏打不了")]
    public void Engineering_and_explicit_tasks_do_not_acquire_casual_continuation(string user)
        => Assert.Equal("none", MashExpressionV02.Select(Context(user)).Intent.FollowUp);

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
