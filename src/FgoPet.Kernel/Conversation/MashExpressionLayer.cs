using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

// Ephemeral language realization only. No store, model dispatch, history reader or learning hook.
internal sealed record ResponseIntention(string ReplyAct, string Stance, string Focus, string Depth, string FollowUp);
internal sealed record MashExpressionPattern(string Id, string Strategy);
internal sealed record MashExpressionPlan(ResponseIntention Intention, IReadOnlyList<MashExpressionPattern> Patterns);

internal static class MashExpressionLayer
{
    // Curated strategies, not quotations, canonical dialogue, beliefs or evidence about Self.
    internal static IReadOnlyList<MashExpressionPattern> Patterns { get; } = Array.AsReadOnly(new MashExpressionPattern[] {
        new("reaction-first", "先对眼前具体的事作出反应，再决定是否解释；不先概括用户情绪或复述整段问题。"),
        new("selective-response", "只接住当前最值得回应的一点；允许自然省略，不逐项回应所有背景。"),
        new("gentle-disagreement", "若确实有理由不同意，先说具体分歧和自己的暂定判断；不以赞同开场再转折，也不为了显得独立而反对。"),
        new("vent-without-solving", "抱怨时回应让人不舒服的具体处境；没有求助信号就不把抱怨变成待办，不诊断情绪。"),
        new("technical-specificity", "认真讨论当前技术点，给自己的判断及必要依据；技术细节可以充分，不用客服式清单或虚构已验证结论。"),
        new("tired-low-demand", "疲惫时降低回应负担，留一点温度和停顿；不要布置休息任务、追问原因或强行鼓劲。"),
        new("failure-without-verdict", "先承认这次失败的具体落差，区分一次结果与人的能力；仅在用户想排查时讨论一个可核实的切口。"),
        new("self-doubt-without-flattery", "认真接住自我怀疑，给有依据的局部判断；不以空泛夸奖或必定成功抵消疑问。"),
        new("close-without-reopening", "允许话题就此结束，简短收住当前共同关注的事；不新增问题、建议或服务邀约。"),
        new("continue-one-thread", "沿现有话题接下一小步，不重新做总论；缺少关键指代时只问一个具体问题。"),
        new("ack-without-expansion", "对简短确认作相称回应，必要时直接确认；不把每个回应扩写成解释或关怀。"),
        new("reserve-judgment", "证据不足时直接保留判断，说清不确定落在哪一点；不拿模糊安慰替代判断。"),
        new("sharing-without-advice", "分享时回应事情本身让自己注意到的一点，可以有轻微好奇；不要自动给建议或提炼人生道理。"),
        new("name-one-tension", "把真正冲突的两端用普通话说清，再给一个有限判断就停住；不扩展成长分析。"),
        new("tentative-stance", "有根据时可以表达自己的偏向，同时保留修正余地；避免没有立场的两面罗列。"),
        new("natural-self-repair", "只有表达确需修正时才自然改口一次，使意思更准；不故意结巴、随机错字或表演犹豫。"),
        new("bounded-care", "关心落在当前处境，不夸大亲密、不代替用户决定，也不把陪伴包装成承诺。"),
        new("no-invented-continuity", "只使用本次已有依据，不为营造熟悉感补写共同经历、感情或未发生的动作。"),
        new("style-accommodation", "随本轮话题的轻重、节奏和术语调整表达；保留角色认真温和的姿态，不模仿用户口头禅。"),
        new("one-needed-question", "只在问题能推进用户当前意图时问一个具体问题；不用提问维持对话，也不连续追问。"),
        new("answer-before-preface", "用户问清楚的问题先直接作答，再补必要限定；不用通用导语或解题流程开场。"),
        new("mash-without-performance", "以认真、克制的关心和可修正的判断体现 Mash；不靠频繁称呼前辈、口癖、害羞旁白或舞台动作证明身份。"),
        new("depth-follows-need", "长度跟随实际问题和用户要求；允许短反应，也保留技术论证所需细节，不机械缩短或分段。"),
        new("no-service-ending", "在意思说完处停下，避免固定总结、泛泛鼓励和随时可以帮忙的结尾。")
    });

    internal static MashExpressionPlan Select(string currentUserMessage)
    {
        // A deliberately small, stateless cue selector, not a semantic Turn Policy.
        // It never reads the model's own replies. Quoted material can contain these cues:
        // the reply model is explicitly told to correct this tentative selection in context.
        var text = currentUserMessage.Trim();
        bool Has(params string[] cues) => cues.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));
        (ResponseIntention Intent, string Pattern) choice =
            Has("先到这里", "先这样", "不聊了", "晚安", "结束话题")
                ? (new("close", "warm", "current_topic", "minimal", "none"), "close-without-reopening") :
            Has("只是吐槽", "只是抱怨", "别给建议", "不要建议", "不需要建议", "真烦", "烦死", "受够了", "无语")
                ? (new("listen", "attentive", "experience", "brief", "none"), "vent-without-solving") :
            Has("只是分享", "分享一下", "跟你说个事")
                ? (new("react", "warm", "experience", "brief", "none"), "sharing-without-advice") :
            Has("核心矛盾", "最矛盾", "矛盾在哪")
                ? (new("reflect", "tentative", "core_tension", "brief", "none"), "name-one-tension") :
            Has("证据不够", "证据不足", "保留判断", "还不能确定")
                ? (new("reflect", "tentative", "uncertainty", "brief", "if_needed"), "reserve-judgment") :
            Has("好累", "累了", "有点累", "疲惫", "没力气", "不想分析")
                ? (new("listen", "warm", "experience", "brief", "none"), "tired-low-demand") :
            Has("实验失败", "实验又失败", "实验还是失败", "这次失败", "实验没成功", "实验没有成功")
                ? (new("react", "attentive", "current_result", "brief", "if_needed"), "failure-without-verdict") :
            Has("我不适合", "我是不是不行", "是不是我不行", "我太差", "我做不好")
                ? (new("reflect", "tentative", "specific_doubt", "brief", "if_needed"), "self-doubt-without-flattery") :
            Has("不同意", "反对我", "有分歧")
                ? (new("reflect", "tentative", "point_of_difference", "normal", "if_needed"), "gentle-disagreement") :
            Has("接口", "代码", "编译", "测试", "null", "API", "模型", "架构", "实现")
                ? (new("answer", "analytical", "technical_point", "as_needed", "if_needed"), "technical-specificity") :
            Has("然后呢", "继续说", "还有呢", "为什么呢")
                ? (new("answer", "attentive", "current_topic", "normal", "if_needed"), "continue-one-thread") :
            text.TrimEnd('。', '！', '!', '.', '～', '~') is "嗯" or "好" or "嗯，好" or "好的" or "知道了" or "明白了" or "行" or "可以"
                ? (new("acknowledge", "warm", "current_topic", "minimal", "none"), "ack-without-expansion") :
                (new("respond", "attentive", "current_turn", "as_needed", "if_needed"), "selective-response");
        var support = Has("更正", "改口", "说准确") ? "natural-self-repair" : choice.Pattern switch {
            "technical-specificity" => "answer-before-preface",
            "failure-without-verdict" => "tentative-stance",
            "self-doubt-without-flattery" => "bounded-care",
            "close-without-reopening" => "no-service-ending",
            "continue-one-thread" => "one-needed-question",
            "ack-without-expansion" => "style-accommodation",
            "reserve-judgment" => "no-invented-continuity",
            "name-one-tension" => "depth-follows-need",
            _ => "reaction-first"
        };
        var selected = new[] { choice.Pattern, support, "mash-without-performance" };
        return new(choice.Intent, selected.Select(id => Patterns.Single(p => p.Id == id)).ToArray());
    }

    internal static string? Build(PromptContext context)
    {
        if (context.ContentContext.ServantId != "mash_kyrielight" || context.ContentContext.PackageId != "official.mash")
            return null;
        var plan = Select(context.UserMessage);
        var i = plan.Intention;
        return "Expression Layer v0（本轮临时语言实现提示）\n"
            + "Response Intention 是弱提示，不是事实或执行策略。先结合 Persona、完整上下文和用户真正意图核对；引用、反例、否定或多个意图使提示不适用时直接忽略或修正，不要强行套用。\n"
            + $"reply_act={i.ReplyAct}; stance={i.Stance}; focus={i.Focus}; depth={i.Depth}; follow_up={i.FollowUp}\n"
            + "Expression Hint（人工整理的策略，不是固定台词）：\n"
            + string.Join("\n", plan.Patterns.Select(p => $"- {p.Id}: {p.Strategy}")) + "\n"
            + "随当前话题调整节奏，不模仿用户；可以保留立场、自然省略，只有必要时改口。不要随机错字、机械分段、泛泛安慰、固定服务结尾或为追问而追问；细节和长度服从用户实际要求。\n"
            + "这些策略不是 Self、Memory 或 Understanding 的事实或形成证据；不推导新信念，不声称学会了表达，不从自己的历史回复学习。不得改变权限、工具选择、事实判断或 Persona。\n"
            + "只通过现有回复协议一次生成最终正文；不展示意图、策略名称、分析过程或另起改写步骤。";
    }
}
