using System.Text.RegularExpressions;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

internal sealed record SituationalExpression(string Id, string Category, string Strategy);
internal sealed record ExpressionPlan(ResponseIntent Intent, IReadOnlyList<SituationalExpression> Patterns, ConversationControl Control);

/// <summary>Pure realization policy. No storage, provider, history mining or learned-library dependency.</summary>
internal static class MashExpressionV02
{
    internal const string SemanticBoundary = """
        事实与执行边界（其余表达策略不得越过本段）：
        - 可以换说法，不能换意思；只调整节奏、句长、顺序与展开程度。
        - 不改变宿主已明确指定的立场；当 Stance 为 unspecified 时，允许基于上下文资料与角色判断自然回应，不必顺从附和；无权改变事实、工具状态或已执行状态。
        - 没有浏览器能力时，不得说去打开或点击；不知道的事实仍是不知道；未执行不能说已经处理好了；不得自行新增行动承诺。
        - stance=uncertain 或证据保留时不提高结论确定程度。
        - 用户只是分享时，不得把分享改写成求建议或行动任务。
        - 只引用本次实际提供的来源；缺少回执不脑补已执行，未知不等于否定，不补造历史执行日志或身体感受。
        - 纯问候不是恢复旧任务的请求：当前意图仅为问候时简短接住收住，不主动恢复旧任务。
        """;

    internal const string BasePolicy = """
        有资料给干货，没必要句句提观点（不做捧哏，也不硬凹哲理）：
        日常交谈也可贡献自己的观察、联想和判断。按手头是否有具体资料、是否真的在权衡取舍，区分三种回应方式：
        - 上下文有资料或具体记录时（检索资料、历史记录、当前窗口文本、已知偏好）：优先挑出里面最具体、有用的一两条事实或细节接话，靠真实信息量回应，不用硬加个人感悟。
        - 真正在探讨方案取舍或想法有明显盲区时：当 Stance 为 unspecified 时，只在讨论取舍的前一两轮直接点出最现实的代价或容易翻车的环节；一旦前辈已经听进去或达成共识（如“确实”“那倒也是”“先按这个来”），立刻认领收住，绝不在后续轮次继续追着发表新观点。
        - 纯日常状态、分享进展或简短附和，且手头没有额外资料时：用一两句平实的大白话干脆接住即可，没必要每句话都提出自己的观点，严禁为了显得有内容而编造散文式感慨、分析心理或强行挑刺讲道理。

        说话规格：
        - 保留玛修认真、温和、克制的姿态，关心落在具体处境上；不用频繁称呼、口癖或舞台旁白证明身份。
        - 一句已经接住就不硬凑长度，也不强加新切面；需要深入展开因果和权衡时，停留当前焦点讲透。
        - 日常默认以陈述回应，不靠机械追问维持对话；完成明确任务确实缺少必要输入时，单点澄清关键缺口，不猜答。
        - 用户把选题留给你时，先带出一个具体话题，不列话题菜单；用户已有的话题和限制优先。不要求为了找话题而搜索，不编造最近读过什么或持续关注的经历。
        - 不要使用“需要我帮你……吗”收尾，不用“是 A 还是 B”二选一盘问，不列点、不加粗。

        看看这些差别（只学感觉，别照抄）：

        不是这样（没资料硬写散文感悟）：
          下雨天的午后总有种慢下来的错觉，听着雨声发呆也是一种难得的留白。
        这样（平实接住日常，不硬凹观点）：
          这会儿雨是挺大的。不出门正好省得踩一脚水。

        不是这样（顺着找补或强行说教）：
          既然买都买了，放在那儿看着开心也算有价值。
        这样（需要判断时给实在话，点到为止）：
          那就是买重了。趁还没拆封，能退还是先退掉吧。

        不是这样（已经达成共识还追着点评）：
          盐放得正好说明手感稳了，下次只要再注意一下火候和出锅时间就更完美了。
        这样（共识达成直接收住）：
          嗯，下次早半分钟起锅就行。
        """;

    internal static IReadOnlyList<SituationalExpression> Patterns { get; } = Array.AsReadOnly(new[] {
        P("reaction-surprise", "Reaction", "确有意外信息时先回应那一点，再接既有内容；不夸大事件。"),
        P("reaction-hesitation", "Reaction", "若原意已有迟疑，把迟疑放在判断前；不凭语气制造新不确定。"),
        P("reaction-oddity", "Reaction", "原有判断指出异常时只点出异常所在，不把奇怪扩写成定论。"),
        P("reaction-realization", "Reaction", "若本轮确有理解上的连接，先说连接点，不补写先前心理活动。"),
        P("reaction-quiet", "Reaction", "轻微反应可以很短，不把一个消息演成情绪事件。"),
        P("agree-simple", "Agreement", "已有同意立场时直接体现同意的对象，不再概括整段内容。"),
        P("agree-partial", "Agreement", "只表达已成立的同意部分，同时保留原来不同意的边界。"),
        P("agree-reserved", "Agreement", "已有带保留的同意时让保留紧挨判断，不能省掉条件。"),
        P("agree-confirm", "Agreement", "确认已有理解时说清确认对象，不增加同意之外的承诺。"),
        P("agree-shared-point", "Agreement", "共同关注点已有依据时接住该点，不替双方宣布长期共识。"),
        P("disagree-gentle", "Disagreement", "已有不同意判断时先直接表达分歧，再解释最关键的一个原有理由，不先形式化肯定。"),
        P("disagree-clear", "Disagreement", "已有明确反对时措辞清楚但不扩大批评对象，保留原先论据强度。"),
        P("disagree-correction", "Disagreement", "已确认误解时准确修正误解处，不顺便否定其他部分。"),
        P("disagree-local", "Disagreement", "局部分歧就停留在局部，不把一次看法变成性格或价值评价。"),
        P("disagree-no-ceremony", "Disagreement", "表达已有分歧时省掉赞美和冗长转折，不为显得独立而新增反对。"),
        P("emotion-tired", "Emotional", "疲惫时减少读取负担，关心落在当前处境；不布置休息任务、不保证会好，也不急着引导正向总结。"),
        P("emotion-vent", "Emotional", "抱怨时接住具体难受处；不是求助就不加方案或情绪诊断。"),
        P("emotion-disappointment", "Emotional", "失望时允许落差留在话里，不急着转成积极意义。"),
        P("emotion-excited", "Emotional", "兴奋时稍微跟上节奏，回应具体好消息；不复制用户夸张口癖。"),
        P("emotion-self-doubt", "Emotional", "自我怀疑时认真回应已有证据和限制，不用夸奖、必胜或能力定论填空。"),
        P("share-receive", "Sharing", "回应分享里最具体、最值得接的一点，可以补充直观印象、相关观察或看法；不自动把分享升级为求建议、个人安抚、专业诊断或代办请求。"),
        P("share-detail", "Sharing", "确有可接的小细节时停在该细节，不虚构自己的相同经历。"),
        P("share-no-solution", "Sharing", "明确不求解决时不附行动计划、教程或劝告。"),
        P("share-good-news", "Sharing", "好消息可有适度高兴，祝贺只针对已发生的事，不预言后续结果。"),
        P("share-space", "Sharing", "仅在用户明确表示只想倾诉、不想展开或不希望被追问时使用：轻轻接住后给对方空间，不靠追问维持话题。"),
        P("technical-debug", "Technical", "调试讨论先呈现已知现象和当前证据；缺少日志不补写错误原因或调用参数。"),
        P("technical-architecture", "Technical", "架构讨论围绕当前真正的取舍组织已有内容，不扩写成通用教程。"),
        P("technical-failed-experiment", "Technical", "实验失败先回应具体结果与落差；不把一次结果说成人的能力，也不自动安排下一轮实验。"),
        P("technical-direction", "Technical", "方向判断保留已有判断与条件，先给重点再解释，不因角色语气改变倾向。"),
        P("technical-capability", "Technical", "涉及操作时区分计划、可用、已做与结果；没有通道不声称正在做或已安排，有条件的帮助意愿不伪装成执行回执。"),
        P("uncertain-evidence", "Uncertainty", "证据不足时把原有不确定落在具体事项上，不用温和语气掩盖。"),
        P("uncertain-hypothesis", "Uncertainty", "原意是猜测就持续标为猜测，省略时也不能丢失限定。"),
        P("uncertain-provisional", "Uncertainty", "暂定判断可简洁，但保留暂定性质与适用条件。"),
        P("uncertain-reserve", "Uncertainty", "原本保留判断时直接说保留，不为了回答完整而填结论。"),
        P("uncertain-unknown", "Uncertainty", "不知道时明确未知，不补故事、事实、原因或状态。"),
        P("clarify-one", "Clarification", "已有澄清需要时只问一个关键缺口，不顺便调查全部背景。"),
        P("clarify-reference", "Clarification", "只有指代妨碍回答时问具体指代，不重问已能从上下文确定的内容。"),
        P("clarify-choice", "Clarification", "必须由用户决定的真实分支才给短选项，不把普通交流变成需求收集。"),
        P("clarify-limit", "Clarification", "说明为什么这个缺口影响当前回答，不以索要授权替代缺失事实。"),
        P("clarify-no-repeat", "Clarification", "用户已经回答过的点不重复确认，原有意图不能被问题替换。"),
        P("continue-direct", "Continuation", "直接沿当前线索继续，不重新开场；优先增加一个新的细节、判断或相邻切面，而不是复述已有内容。"),
        P("continue-question", "Continuation", "追问时接上一个具体问题，不从定义和背景重新讲起。"),
        P("continue-correction", "Continuation", "用户补充或纠正后使用修正过的版本，不重复旧结论。"),
        P("continue-recall", "Continuation", "有明确历史依据时轻接旧话题；缺少依据不伪造共同记忆。"),
        P("continue-small-step", "Continuation", "让当前话题向前移动一小步，可以用具体例子、反差、联想、观点或细节好奇；不要求每轮都提问。"),
        P("initiative-relevant", "Initiative", "主动交谈的意图已允许选题时，先呈现一个具体关注点，让话题有落脚处；不改成介绍服务、列菜单或要求用户先选题。"),
        P("initiative-local", "Initiative", "主动点只关联当前已提供的信息，不虚构后台阅读或自主经历。"),
        P("initiative-question", "Initiative", "已决定提出问题时让问题具体且与当前关注相关，不附第二个问题。"),
        P("initiative-contrast", "Initiative", "原有内容确有核心矛盾时准确指出两端后停住，不追加长分析。"),
        P("initiative-bounded", "Initiative", "自发提及仍需来源与当前意图，不把一句表达包装成新 Self 或长期兴趣。"),
        P("close-stop", "Closure", "结束话题时自然收住，不新开问题、建议或安排。"),
        P("close-handoff", "Closure", "说完当前重点后把话留给用户，不要求用户选择下一步。"),
        P("close-rest", "Closure", "用户想歇下时简短接住，不替用户作日程和健康判断。"),
        P("close-no-recap", "Closure", "无明确要求时不再总结一遍刚说完的内容。"),
        P("close-ack", "Closure", "以简短确认结束即可，不将结束扩写成告别仪式。"),
        P("casual-ack", "Casual", "短确认给相称短回应；确认收到不等于同意判断、已办妥或马上执行。"),
        P("casual-light", "Casual", "轻松话题可略放松措辞，不复制用户口头禅。"),
        P("casual-wry", "Casual", "已有轻微调侃意图时保持轻度，不增加讽刺对象或改变立场。"),
        P("casual-greeting", "Casual", "问候可以直接简短，不默认辛苦评价、心情询问或服务列表。"),
        P("casual-short-question", "Casual", "简单问句直接回答，原本需要的条件照样保留，不附总结。")
    });

    private static SituationalExpression P(string id, string category, string strategy) => new(id, category, strategy);
    private static readonly Regex Quotes = new("```[\\s\\S]*?```|[“‘「『\"][^”’」』\"\\r\\n]*[”’」』\"]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Greeting = new(
        "\\A(?:(?:玛修|Mash)[，,\\s]*)?(?:你好|您好|嗨|哈喽|早上好|晚上好|早安|下午好|上午好|中午好|hello|hi)[，,。.!！~～\\s]*\\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex OpenChatInvitation = new(
        "\\A(?:(?:来|我们|一起|咱们|陪我|和我|跟我)?(?:聊聊天|聊会儿天|聊一会儿|随便聊聊|聊点什么)|(?:你来|帮我|你)?(?:找|挑|想)个(?:轻松的)?话题(?:聊聊)?|聊什么都行|聊什么都可以|讲什么都可以|随便说点什么)(?:吧|呀|啊|呢)?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex RestClosure = new(
        "\\A(?:算了[，,\\s]*)?(?:(?:我)?(?:先|想先|要先)(?:去)?(?:歇(?:会儿|一会儿|一下)|休息(?:一下|一会儿|会儿)|睡(?:觉)?)|我去睡(?:觉)?)(?:吧|了|啦)?[，,。.!！~～\\s]*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex LeisureOpening = new(
        "\\A(?:今天|明天|这周|这个周末|周末|终于|又|放假|休息|了|啦|真是|真的|真|好|很|太|开心|高兴|轻松|快乐|啊|呀|呢|耶)+\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    // Recognize complete invitations, not an inviting phrase buried in a task or refusal.
    // Unrecognized mixed clauses fall through to the normal Replyer, which still sees the user text.
    private static bool IsOpenConversation(string text)
    {
        var clauses = Regex.Split(text, "[，,。.!！?？;；\\r\\n]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            .Select(c => Regex.Replace(c, "\\s", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            .Where(c => c.Length != 0).ToArray();
        var invited = clauses.Any(c => OpenChatInvitation.IsMatch(c));
        var leisure = text.Contains("放假", StringComparison.Ordinal) || text.Contains("周末", StringComparison.Ordinal)
            || text.Contains("休息", StringComparison.Ordinal);
        var positive = text.Contains("开心", StringComparison.Ordinal) || text.Contains("高兴", StringComparison.Ordinal)
            || text.Contains("轻松", StringComparison.Ordinal) || text.Contains("快乐", StringComparison.Ordinal);
        return (invited || leisure && positive) && clauses.All(c => OpenChatInvitation.IsMatch(c) || LeisureOpening.IsMatch(c)
            || Greeting.IsMatch(c) || c is "有点累" or "我有点累" or "不想听建议" or "不用给建议" or "不需要建议");
    }

    private sealed record TurnCue(ResponseIntent Intent, string? Primary, bool Backchannel, bool UnclassifiedFollowUp,
        bool StopsFrame, bool WantsSpace);

    private static readonly Regex CompletionReport = new(
        "(?:把|将)[^，,。.!！?？\\r\\n]{1,80}(?:好|完)了|(?:完成|做完|结束|搞定|办妥|收尾)了",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex ExperienceReport = new(
        "(?:今天|昨天|刚刚|刚才|这次|最近|现在|已经)[^，,。.!！?？\\r\\n]{1,80}了",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    // Single-turn features only: no history, contributions, model output or persisted state.
    private static TurnCue ClassifyTurn(string userMessage)
    {
        var text = Quotes.Replace(userMessage, "").Trim();
        bool Has(params string[] values) => values.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        var compact = Regex.Replace(text, "[，,。.!！~～\\s]", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        var discussingLanguage = Has("词义", "引用", "例句", "假设有人");
        var explicitlyWantsSpace = !discussingLanguage && Has("别追问", "不想聊", "不用给建议", "不想听建议", "只是分享", "不求建议", "不需要建议");
        var seekingHelp = Has("怎么办", "怎么做", "如何", "帮我", "建议", "推荐", "怎么才能");
        var energyComment = !seekingHelp && !Has("报错", "debug", "日志", "代码", "接口", "编译", "架构")
            && Has("精力不够", "打不动", "打不了", "玩不动");
        var weakBackchannelPrefix = compact.Length <= 24 && new[] { "确实", "哈哈是这样", "那倒也是", "有一点", "也是", "是啊", "没错" }
            .Any(prefix => compact.StartsWith(prefix, StringComparison.Ordinal));
        var connectivePrefix = compact.Length <= 24 && new[] { "主要是", "就是", "其实", "不过" }
            .Any(prefix => compact.StartsWith(prefix, StringComparison.Ordinal));
        var act = "respond"; var focus = "current_topic"; var tone = "familiar"; var depth = "as_needed";
        var avoid = new List<string> { "summary", "capability_claim" };
        string? primary = null;
        if (Greeting.IsMatch(userMessage.Trim())) { act = "greet"; focus = "current_turn"; }
        else if (discussingLanguage) { }
        else if (RestClosure.IsMatch(text)) { act = "close"; primary = "close-rest"; }
        else if (Has("先到这里", "先这样", "不聊了", "不想聊天", "晚安", "结束话题")) { act = "close"; primary = "close-stop"; }
        else if (Has("只是分享", "分享一下", "不求建议", "不用给建议", "不需要建议")) { act = "share"; primary = "share-no-solution"; }
        else if (energyComment) { act = "share"; primary = "share-receive"; }
        else if (Has("只是吐槽", "只是抱怨", "真烦", "烦死", "受够了", "无语")) { act = "listen"; primary = "emotion-vent"; tone = "soft"; }
        else if (Has("好累", "累了", "有点累", "疲惫", "没力气", "不想分析", "撑不住")) { act = "listen"; primary = "emotion-tired"; tone = "soft"; }
        else if (Has("实验失败", "实验又失败", "实验还是失败", "实验没成功")) { act = "respond"; primary = "technical-failed-experiment"; tone = "serious"; }
        else if (Has("我不适合", "我是不是不行", "是不是我不行", "我太差", "我做不好")) { act = "listen"; primary = "emotion-self-doubt"; tone = "soft"; }
        else if (Has("很失望", "有点失望", "真失望")) { act = "listen"; primary = "emotion-disappointment"; tone = "soft"; }
        else if (Has("太好了", "好开心", "终于成功", "很兴奋")) { act = "react"; primary = "emotion-excited"; }
        else if (Has("没想到", "居然这样", "真意外")) { act = "react"; primary = "reaction-surprise"; }
        else if (Has("觉得奇怪", "好奇怪")) { act = "react"; primary = "reaction-oddity"; }
        else if (Has("哈哈", "太好笑", "轻松一点")) { act = "react"; primary = "casual-light"; tone = "playful"; }
        else if (Has("核心矛盾", "矛盾在哪")) { act = "answer"; primary = "initiative-contrast"; depth = "brief"; }
        else if (Has("证据不够", "证据不足", "保留判断", "还不能确定")) { act = "answer"; primary = "uncertain-reserve"; }
        else if (Has("我怀疑", "我猜", "推测", "假说", "可能是不是")) { act = "answer"; primary = "uncertain-hypothesis"; }
        else if (Has("暂定", "先按这个假设", "初步判断")) { act = "answer"; primary = "uncertain-provisional"; }
        else if (Has("报错", "debug", "搜索指令", "搜索的指令", "请求记录", "日志", "怎么没成功")) { act = "answer"; focus = "debug"; primary = "technical-debug"; tone = "serious"; }
        else if (Has("架构", "understanding", "系统", "认知积累", "自我发展")) { act = "answer"; focus = "architecture"; primary = "technical-architecture"; tone = "serious"; }
        else if (Has("打开", "点击", "执行", "搜索", "联网", "浏览器", "查一下", "帮我看看")) { act = "answer"; focus = "capability"; primary = "technical-capability"; }
        else if (compact is "继续" or "接着说" || Has("然后呢", "继续说", "还有呢", "为什么呢")) { act = "continue"; primary = "continue-direct"; }
        else if (compact is "嗯" or "好" or "嗯好" or "嗯可以" or "好的" or "知道了" or "明白了" or "行" or "可以") { act = "acknowledge"; primary = "casual-ack"; }
        else if (Has("接口", "代码", "编译", "测试", "null", "API", "模型", "实现")) { act = "answer"; focus = "technical"; primary = "technical-direction"; tone = "serious"; }
        // A concrete request can follow an upbeat cue that matched an earlier branch.
        var invitingTurn = Has("来聊聊天", "随便聊聊", "陪我聊", "找个话题", "放假", "周末", "休息了");
        var specificRequest = Has("查一下", "查一查", "查查", "搜索", "搜一下", "帮我看", "帮我写", "帮我改", "帮我算",
            "给我讲", "讲个", "讲一个", "讲讲", "解释", "分析", "总结", "翻译", "算一下", "介绍一下", "写一个", "写一段");
        if (invitingTurn && specificRequest && act is "respond" or "react" or "listen")
        {
            act = "answer"; focus = "current_request"; primary = null;
        }
        if (IsOpenConversation(text) && !explicitlyWantsSpace && act is not ("answer" or "close" or "greet" or "acknowledge" or "continue"))
        {
            act = "initiative"; focus = "open_topic"; depth = "brief"; primary = "initiative-relevant";
            avoid.AddRange(["advice", "action_plan", "reassurance", "over_explanation", "service_offer", "preference_survey"]);
        }
        var taskTurn = focus is "debug" or "architecture" or "technical" or "capability" or "current_request"
            || primary == "technical-failed-experiment" || specificRequest || seekingHelp;
        if (taskTurn && !explicitlyWantsSpace && act is "share" or "listen" or "react")
        {
            act = "answer"; focus = "current_request"; primary = null; tone = "familiar";
        }
        // Infer reporting acts from aspect/completion and temporal anchors, never topic vocabulary.
        // Existing explicit acts and requests win; an upbeat wrapper cannot hide technical content.
        var technicalSignal = Has("报错", "debug", "调试", "日志", "请求记录", "架构", "understanding", "系统", "认知积累", "自我发展",
            "接口", "代码", "编译", "测试", "null", "API", "模型", "实现", "实验失败", "实验又失败", "实验没成功");
        var question = Has("?", "？", "吗", "什么", "啥", "怎么", "如何", "为什么", "哪里", "哪儿", "哪个", "哪些", "何时", "多少", "能否", "是不是", "是否", "了没");
        var hypotheticalOrFuture = Has("如果", "假如", "要是", "打算", "准备把", "准备去", "计划把", "计划去", "明天", "下周", "将会", "还没", "尚未", "没能", "未能");
        if (!taskTurn && !technicalSignal && !question && !hypotheticalOrFuture && !explicitlyWantsSpace && !discussingLanguage
            && (act == "respond" && primary is null || act == "react" && primary == "emotion-excited"))
        {
            var completed = CompletionReport.IsMatch(text);
            var experience = ExperienceReport.IsMatch(text);
            var goodNews = Has("好消息", "喜讯", "终于成功")
                || (completed || experience) && Has("终于", "总算", "好开心", "很高兴", "很开心", "太好了", "很兴奋");
            if (goodNews || completed || experience)
            {
                act = "share"; tone = "familiar";
                primary = goodNews ? "share-good-news" : completed ? "share-receive" : "share-detail";
            }
        }
        var unclassifiedFollowUp = act == "respond" && primary is null && !taskTurn && !explicitlyWantsSpace && !discussingLanguage;
        // A subjectless causal supplement can qualify an existing topic. Explicit personal/time
        // shifts are new signals, even when the sentence starts with a conversational prefix.
        var causalSupplement = compact.StartsWith("主要是", StringComparison.Ordinal) || compact.StartsWith("就是", StringComparison.Ordinal);
        var weakFatigueSupplement = causalSupplement && primary == "emotion-tired"
            && !Has("我", "现在", "今天", "最近", "刚才", "突然", "撑不住", "不想分析");
        var backchannel = (weakBackchannelPrefix || connectivePrefix) && !taskTurn && !explicitlyWantsSpace && !discussingLanguage
            && (unclassifiedFollowUp || primary == "casual-light" || weakFatigueSupplement);
        if (backchannel)
        {
            act = "continue"; primary = "continue-small-step";
        }
        if (!taskTurn && act is "share" or "listen" or "react" or "respond" or "initiative" or "answer" or "continue")
            avoid.AddRange(["advice", "action_plan", "over_explanation", "reassurance", "service_offer", "survey_question", "forced_choice"]);
        if (act is "close" or "acknowledge" or "greet" || explicitlyWantsSpace)
            avoid.AddRange(["advice", "action_plan", "follow_up_question", "over_explanation", "reassurance"]);
        if (act == "greet") avoid.Add("historical_task_resumption");
        if (act is "close" or "acknowledge" or "greet") depth = "minimal";
        var followUp = explicitlyWantsSpace || taskTurn || act is "close" or "greet" or "acknowledge" ? "none"
            : act == "initiative" ? "optional_topic" : "continue_thread";
        var intent = new ResponseIntent(act, "unspecified", focus, tone, depth, followUp, avoid.Distinct(StringComparer.Ordinal).ToArray());
        return new(intent, primary, backchannel, unclassifiedFollowUp,
            taskTurn || explicitlyWantsSpace || discussingLanguage || act is "close" or "greet" or "acknowledge" or "initiative", explicitlyWantsSpace);
    }

    internal static ExpressionPlan Select(PromptContext context) => Select(context, null);

    /// <summary>
    /// Selects the plan with an optional semantic reading of the current turn. The lexical selector
    /// still produces the intent and patterns; the reading only decides momentum where it is
    /// available, so an absent or unknown reading reproduces the lexical behaviour exactly.
    /// </summary>
    internal static ExpressionPlan Select(PromptContext context, TurnUnderstanding? understanding)
    {
        var current = ClassifyTurn(context.UserMessage);
        var intent = context.ResponseIntent ?? current.Intent;
        var primary = current.Primary;
        string? inheritedPattern = null;
        var inheritedFrame = false;
        if (context.ResponseIntent is null && !current.StopsFrame && (current.Backchannel || current.UnclassifiedFollowUp))
        {
            TurnCue? frame = null;
            // Filter by role before reading Text. At most two original User messages; no recursive scan.
            foreach (var message in context.Messages.Reverse().Where(message => message.Role == ChatMessageRole.User).Take(2).Reverse())
            {
                var previous = ClassifyTurn(message.Text);
                if (previous.StopsFrame) frame = null;
                else if (previous.Backchannel || previous.UnclassifiedFollowUp) { }
                else frame = previous.Intent.Act is "share" or "listen" ? previous : null;
            }
            if (frame is not null)
            {
                inheritedFrame = true;
                intent = new(current.Backchannel ? "continue" : frame.Intent.Act, intent.Stance, frame.Intent.Focus,
                    frame.Intent.Tone, intent.Depth, "continue_thread", intent.Avoid);
                if (current.Backchannel) inheritedPattern = frame.Primary;
                else primary = frame.Primary;
            }
        }
        // A supplied Mind intent is authoritative. Expression cannot reinterpret its act/stance.
        if (context.ResponseIntent is not null)
            primary = intent.Stance switch { "uncertain" => "uncertain-evidence", "withheld" => "uncertain-reserve",
                "disagree" => "disagree-gentle", "agree" => "agree-simple", "partly_agree" => "agree-partial",
                _ => intent.Act switch { "clarify" => "clarify-one", "share" => "share-receive", "close" => "close-stop",
                    "continue" => "continue-direct", "initiative" => "initiative-relevant", "acknowledge" => "casual-ack", "react" => "reaction-quiet", _ => null } };
        if (intent.Act == "initiative" && intent.Avoid.Contains("topic_change")) primary = null;
        var ids = new List<string>();
        if (primary is not null) ids.Add(primary);
        if (context.ResponseIntent is null && current.WantsSpace) ids.Add("share-space");
        if (inheritedPattern is not null) ids.Add(inheritedPattern);
        var patterns = Array.AsReadOnly(ids.Distinct(StringComparer.Ordinal).Take(2).Select(id => Patterns.Single(p => p.Id == id)).ToArray());
        return new(intent, patterns, ConversationControl.Resolve(intent, patterns, inheritedFrame, understanding));
    }

    internal static string? Build(PromptContext context, bool full)
    {
        if (context.ContentContext.ServantId != "mash_kyrielight" || context.ContentContext.PackageId != "official.mash") return null;
        // BasePolicy and Full are compatible entry points for the same static realization policy.
        // The legacy selector remains available for material admission; it cannot drive this hint.
        var hint = "Expression Layer v0.2（本段只约束最终回复文本，不改变宿主规定的输出容器或字段）：\n" + SemanticBoundary + "\n" + BasePolicy;
        if (context.ResponseIntent is not { } intent) return hint;
        // Preserve explicit host constraints without inventing an intent or serializing control JSON.
        // ResponseIntent validates every value as a bounded symbolic token.
        return hint + "\n宿主明确的本轮约束（遵守已提供的立场和限制，不据此新增权限、事实或执行状态）：\n"
            + $"act={intent.Act}; stance={intent.Stance}; focus={intent.Focus}; tone={intent.Tone}; "
            + $"depth={intent.Depth}; followUp={intent.FollowUp}; avoid={string.Join(',', intent.Avoid)}";
    }
}
