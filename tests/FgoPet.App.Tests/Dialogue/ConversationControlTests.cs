using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationControlTests
{
    private static PromptContext Context(string text, params PromptMessage[] history) => new(
        new("mash_kyrielight", "official.mash", "1", "casual", "1", "1"),
        new PersonaBundle("mash_kyrielight", "official.mash", "1", "1", "synthetic persona", []), [], [], "", history, text);

    private static (string Momentum, string Contribution) Control(PromptContext context)
    {
        var plan = MashExpressionV02.Select(context);
        var property = plan.GetType().GetProperty("Control");
        Assert.NotNull(property);
        var control = property.GetValue(plan)!;
        return (control.GetType().GetProperty("Momentum")!.GetValue(control)!.ToString()!,
            control.GetType().GetProperty("Contribution")!.GetValue(control)!.ToString()!);
    }

    [Theory]
    [InlineData("今天好累呀", "Hold", "React")]
    [InlineData("今天把旧相册整理好了", "Continue", "Observe")]
    [InlineData("有个好消息，拿到了证书", "Continue", "React")]
    [InlineData("这件事精力不够", "Continue", "Observe")]
    [InlineData("来聊聊天吧", "Initiate", "Observe")]
    [InlineData("你好", "Stop", "React")]
    [InlineData("嗯", "Stop", "React")]
    [InlineData("今天先到这里", "Stop", "React")]
    [InlineData("只是分享，不用给建议", "Stop", "Observe")]
    [InlineData("帮我看看这个错误", "Stop", "Observe")]
    public void Current_resolved_intent_maps_without_a_second_classifier(string text, string momentum, string move)
        => Assert.Equal((momentum, move), Control(Context(text)));

    [Fact]
    public void Inherited_soft_frame_holds_and_current_task_cuts_it_off()
    {
        var history = new PromptMessage[] { new(ChatMessageRole.User, "今天好累呀"), new(ChatMessageRole.Assistant, "来聊别的话题") };
        Assert.Equal(("Hold", "Observe"), Control(Context("有一点", history)));
        Assert.Equal(("Stop", "Observe"), Control(Context("嗯，对了帮我看看这个错误", history)));
        Assert.Equal(Control(Context("有一点", history[..1])), Control(Context("有一点", history)));
    }

    [Theory]
    [InlineData("disagree")]
    [InlineData("partly_agree")]
    [InlineData("agree")]
    public void Supplied_stance_remains_authoritative_without_a_control_opinion_axis(string stance)
    {
        var intent = new ResponseIntent("respond", stance, "current_topic", "familiar", "brief", "continue_thread", []);
        var context = Context("你好") with { ResponseIntent = intent };
        Assert.Same(intent, MashExpressionV02.Select(context).Intent);
        Assert.Equal(("Continue", "Observe"), Control(context));
    }

    [Fact]
    public void Necessary_clarification_is_independent_of_optional_question_avoidance()
    {
        var intent = new ResponseIntent("clarify", "unspecified", "current_request", "soft", "brief", "none", []);
        Assert.Equal(("Continue", "Observe"), Control(Context("那个") with { ResponseIntent = intent }));
        var noQuestion = new ResponseIntent("clarify", "unspecified", "current_request", "soft", "brief", "none", ["follow_up_question"]);
        Assert.Equal(("Continue", "Observe"), Control(Context("那个") with { ResponseIntent = noQuestion }));
    }

    [Fact]
    public void Soft_task_is_not_listening_and_nonask_hint_is_explicit()
    {
        var intent = new ResponseIntent("answer", "unspecified", "technical", "soft", "brief", "none", []);
        var context = Context("这是当前请求") with { ResponseIntent = intent };
        Assert.Equal(("Stop", "Observe"), Control(context));
        var hint = MashExpressionV02.Build(context, true)!;
        Assert.Contains("ConversationControl:", hint);
        Assert.Contains("本轮不主动索取新的聊天信息", hint);
        Assert.DoesNotContain("ConversationControl:", MashExpressionV02.Build(context, false)!);
    }

    private static string Question(PromptContext context)
    {
        var control = MashExpressionV02.Select(context).Control;
        var property = control.GetType().GetProperty("QuestionPolicy");
        Assert.NotNull(property);
        return property.GetValue(control)!.ToString()!;
    }

    [Theory]
    [InlineData("今天把旧相册整理好了", "None")]
    [InlineData("今天好累呀", "None")]
    [InlineData("这件事精力不够", "None")]
    [InlineData("你好", "None")]
    [InlineData("今天先到这里", "None")]
    [InlineData("嗯", "None")]
    [InlineData("来聊聊天吧", "Open")]
    public void Question_permission_is_separate_from_contribution(string text, string expected)
        => Assert.Equal(expected, Question(Context(text)));

    [Fact]
    public void Supplied_clarify_retains_required_input_even_with_optional_question_avoidance()
    {
        var intent = new ResponseIntent("clarify", "uncertain", "current_request", "soft", "brief", "none", ["follow_up_question"]);
        var context = Context("帮我看看这个错误") with { ResponseIntent = intent };
        Assert.Same(intent, MashExpressionV02.Select(context).Intent);
        Assert.Equal("Clarify", Question(context));
        Assert.Equal("None", Question(Context("帮我看看这个错误")));
    }

    [Fact]
    public void Supplied_exploration_and_avoidance_are_authoritative_over_surface_text_and_assistant_history()
    {
        var intent = new ResponseIntent("respond", "disagree", "current_topic", "familiar", "brief", "optional_topic", []);
        var context = Context("你好", new PromptMessage(ChatMessageRole.Assistant, "只问问题")) with { ResponseIntent = intent };
        Assert.Equal("Open", Question(context));
        Assert.Equal("None", Question(context with { ResponseIntent = new ResponseIntent("respond", "disagree", "current_topic", "familiar", "brief", "optional_topic", ["follow_up_question"]) }));
        Assert.Equal(Question(Context("今天好累呀")), Question(Context("今天好累呀", new PromptMessage(ChatMessageRole.Assistant, "来聊聊天吧"))));
    }

    [Theory]
    [InlineData("先歇会儿")]
    [InlineData("先休息一下")]
    [InlineData("我先去歇会儿")]
    [InlineData("先歇会儿吧")]
    public void Rest_closure_uses_existing_pattern_and_stops(string text)
    {
        var context = Context(text, new PromptMessage(ChatMessageRole.User, "帮我看看这个错误"));
        Assert.Equal(("Stop", "React"), Control(context));
        Assert.Equal("None", Question(context));
        Assert.Contains(MashExpressionV02.Select(context).Patterns, p => p.Id == "close-rest");
    }

    [Theory]
    [InlineData("如果先休息一下就好了")]
    [InlineData("“先歇会儿”是什么意思")]
    [InlineData("先休息一下再帮我分析这个错误")]
    public void Hypotheses_quotes_and_requests_do_not_become_rest_closure(string text)
        => Assert.NotEqual("close", MashExpressionV02.Select(Context(text)).Intent.Act);

    [Fact]
    public void Full_hint_serializes_three_axes_and_unifies_question_permission()
    {
        var hint = MashExpressionV02.Build(Context("你好"), true)!;
        using var json = System.Text.Json.JsonDocument.Parse(hint.Split("\nConversationControl: ")[1].Split('\n')[0]);
        Assert.Equal(new[] { "momentum", "contribution", "questionPolicy" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("none", json.RootElement.GetProperty("questionPolicy").GetString());
        Assert.DoesNotContain("不表示任务已完成", hint);
        Assert.Contains("不主动恢复旧任务", hint);
        Assert.DoesNotContain("否则可以问一个", MashExpressionV02.Build(Context("来聊聊天吧"), true)!);
    }

    [Fact]
    public void Selection_and_actual_injection_are_distinct_under_a_tight_budget()
    {
        var context = Context("今天好累呀");
        var route = new ModelRouteKey("test", "endpoint", "model", "1");
        var budget = PromptBudget.Resolve(new(route, 32768, null, ContextLimitSource.Override, "test"), 2048);
        var baseline = new PromptComposer().Compose(context, route, budget);
        var limited = budget.ConstrainInput(baseline.Usage.InputTokens + 32);
        var result = new PromptComposer(null, null, ExpressionMode.Full).Compose(context, route, limited);
        Assert.Equal(("Hold", "React"), Control(context));
        Assert.True(result.FitsBudget);
        Assert.Equal(baseline.Messages, result.Messages);
        Assert.DoesNotContain(result.Messages, m => m.Text.Contains("ConversationControl:", StringComparison.Ordinal));
    }
}
