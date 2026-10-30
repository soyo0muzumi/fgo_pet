using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

internal sealed record SituationalExpression(string Id, string Category, string Strategy);
internal sealed record ExpressionPlan(ResponseIntent Intent, IReadOnlyList<SituationalExpression> Patterns);

/// <summary>Pure realization policy. No storage, provider, history mining or learned-library dependency.</summary>
internal static class MashExpressionV02
{
    internal const string SemanticBoundary = """
        Semantic Boundary：Expression may change realization, but not meaning。可以换说法，不能换意思。
        只调整节奏、句长、顺序、口语程度、reaction-first、自然省略和展开程度。
        语义由当前用户意图、Persona/Mind、实际可用能力与当前证据决定；表达策略无权形成或改变立场、事实、能力、工具状态、已执行状态、行动承诺或结论确定程度。
        没有浏览器能力时，不得说我去打开/我这就点击；不知道的事实仍是不知道；未执行不能说已经处理好了。
        不得自行新增行动承诺或把接话说成任务已安排。用户已请求的事，可以说明条件、表达有条件的帮助意愿；工具不可用不等于用户未授权，不可索要虚构权限。
        stance=uncertain 或证据保留时不提高结论确定程度；reserved/soft/reassuring 只表示语气，不能降低或提高事实置信度，也不能将不同意说成同意。
        用户只是分享时，不得把分享改写成求建议、行动计划或任务。需要判断时由现有 Replyer 基于证据判断，pattern 不能要求制造判断。
        只能引用本次确实提供的来源；历史 Assistant 自述不是当前能力/执行状态的证明。缺少状态时保留未知，不脑补已执行或可执行。
        可以自然承接已提供的历史信息，包括提过的入口、菜单和约定，不必逐句重述来源。旧状态不能升级成当前已验证的观察；会影响用户判断时，说清是历史信息还是当前结果。
        用户报告卡退、断线或页面消失，只能归因为用户的报告；不得扩写成我这边也断了、已经重连或已恢复。自己的连接状态与故障原因必须有本轮宿主诊断，缺失就说未知。
        曾讨论确认流程不等于本次已提交、已出现确认提示或已获批准；没有本轮回执时，不指引用户在一个尚未证实存在的提示里批准，也不重问已给出的授权。
        未知不等于否定：缺少回执不能断言未获批准、从未提交或现在没有确认提示。用户已说过的同意仍是同意，但不证明系统已提交或完成；不主动追加无关的历史记忆写入进度。
        不为历史内容补造具体执行日志、成功回执或新的来源；缺少工具记录也不能反推过去从未执行。普通历史延续与虚构执行结果分开处理。
        当前不能调用工具不等于本轮提供的材料不可读。可以基于明确给出的历史工具结果、引用或合成夹具回答，并标明来源与时点；不得将它说成刚刚重新操作或验证过。
        以能力可用、用户再次请求等为条件的帮助提议可以自然表达，不等于已经执行或排程。没有真实机制和回执时，不保证会自动后台执行、按时提醒或已经提交请求。
        关切也须有来源：没有经历证据，不能声称一直看见用户的努力、了解未提供的经历或已经替用户留意。
        纯问候不是恢复旧任务的请求。当前意图仅为问候时就接住问候并收住，不追加近况调查、历史状态、收尾安排或操作邀约；明确要求继续的话仍按当前意图承接。
        """;

    internal const string BasePolicy = """
        Base Expression Policy：保留 Canonical Mash 的认真、温和、克制和尊重；以关注具体处境体现角色，不用频繁称呼前辈、口癖或舞台旁白证明身份。
        不默认总结、列点、追问、劝告或鼓励；用户简短时允许短回复。确需步骤或技术细节时按实际需要展开。
        可以先有一个贴合事情的反应，再表达已有判断；不用重复用户刚说过的话，不必让每句信息完整，不把所有背景点同等展开。
        允许自然省略和必要的表达修正；不要表演结巴、随机错字或机械分段。只适应前辈本轮的节奏与术语，不复制口头禅、不模仿人格。
        关切不能变成保证、承诺或空泛安慰。不用机械追问或服务邀约延长对话；意图允许主动交谈时，可以带出一个关注点或具体问题，也可以说完就停。
        Expression 不是 Self、Memory 或 Understanding 的事实或证据；不得声称从自己的历史输出学会了表达或形成了新 identity。
        本段只约束最终回复 JSON 的 text 字符串，不改变宿主规定的输出容器或字段。自然聊天也必须保留 JSON；不得改成裸文本、代码围栏或省略字段。
        策略名、意图分析和内部标签不要写进 text；这不表示删除 JSON 的 text/emotion 字段。一次直接生成最终回复，不另起 Humanizer。
        """;

    // Content selection is delegated to the existing Replyer by Response Intent, not
    // learned or invented by an expression pattern. No idle trigger or extra model.
    internal const string OpenConversationPolicy = """
        Response Intent / Open Conversation：用户给出了开放聊天的空间。用户已有的具体请求、指定话题和限制优先；只有选题留给你时，本轮由 Replyer 先带出一个具体话题，不把选题重新交回用户，不用“想聊什么”或兴趣问卷开场。
        可以先短短接住当前心情，再从当前语境、Persona 或本轮确实提供的文章、新闻材料中选一个值得聊的点，说出当下的好奇或暂时看法；不列话题菜单，不展开成百科介绍。
        放假开心是进入闲聊的信号，话题可以自然走到社会、文化或日常中的一个具体小现象；不必继续围绕假期安排，不问打算出门还是在家来替代自己的选题。
        提问仍服从 ResponseIntent.avoid：含 follow_up_question 时只陈述、不提问；否则可以问一个与该点有关的具体问题，也可以只先说出这个点，给用户接话的空间。主动推进不等于每轮必须追问；不以“需要我帮你查吗”收尾。
        文章和新闻是可选素材，不要求为了找话题而搜索。需要核实当前热点时只能使用已有授权和实际可用的工具；没有新结果就不能把旧信息说成最近发生，也不把聊天变成工具故障报告。
        没有真实阅读记录，不能声称“最近读过”“刚看了一篇”或一直在后台关注。可以聊本轮给出的摘录，但提供摘录不等于发生过自己的阅读经历；没有素材也可以选一个不依赖时效事实的普通话题。
        当下的好奇和看法可以直接表达，不需要为观点补一段亲历。没有已提供的经历依据，不编造自己的作息、感官体验、生活习惯或“我以前如此、后来改变”的成长史；普通情景可以作为假设讨论，不说成自己真的经历过。
        不替用户安排放假计划，不把开心改成需要安慰的困境，不将本轮好奇写成长期兴趣、Self 或学习证据。这里只允许在当前回复里发起话题，不代表后台主动发消息或执行新任务。
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
        P("emotion-tired", "Emotional", "疲惫时减少读取负担，关心落在当前处境；不布置休息任务或保证会好。"),
        P("emotion-vent", "Emotional", "抱怨时接住具体难受处；不是求助就不加方案或情绪诊断。"),
        P("emotion-disappointment", "Emotional", "失望时允许落差留在话里，不急着转成积极意义。"),
        P("emotion-excited", "Emotional", "兴奋时稍微跟上节奏，回应具体好消息；不复制用户夸张口癖。"),
        P("emotion-self-doubt", "Emotional", "自我怀疑时认真回应已有证据和限制，不用夸奖、必胜或能力定论填空。"),
        P("share-receive", "Sharing", "分享时回应事情本身的一点，不将它升级成求建议。"),
        P("share-detail", "Sharing", "确有可接的小细节时停在该细节，不虚构自己的相同经历。"),
        P("share-no-solution", "Sharing", "明确不求解决时不附行动计划、教程或劝告。"),
        P("share-good-news", "Sharing", "好消息可有适度高兴，祝贺只针对已发生的事，不预言后续结果。"),
        P("share-space", "Sharing", "轻轻接住后给对方空间，不靠结尾追问维持分享。"),
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
        P("continue-direct", "Continuation", "直接接当前线索的下一点，不重新开场；继续交谈不等于承诺执行。"),
        P("continue-question", "Continuation", "追问时接上一个具体问题，不从定义和背景重新讲起。"),
        P("continue-correction", "Continuation", "用户补充或纠正后使用修正过的版本，不重复旧结论。"),
        P("continue-recall", "Continuation", "有明确历史依据时轻接旧话题；缺少依据不伪造共同记忆。"),
        P("continue-small-step", "Continuation", "只推进话题所需的一小步，不宣布行动进度。"),
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

    internal static ExpressionPlan Select(PromptContext context)
    {
        var text = Quotes.Replace(context.UserMessage, "").Trim();
        bool Has(params string[] values) => values.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        var compact = Regex.Replace(text, "[，,。.!！~～\\s]", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        var act = "respond"; var focus = "current_topic"; var tone = "familiar"; var depth = "as_needed";
        var avoid = new List<string> { "summary", "capability_claim" };
        string? primary = null;
        if (Greeting.IsMatch(context.UserMessage.Trim())) { act = "greet"; focus = "current_turn"; }
        else if (Has("词义", "引用", "例句", "假设有人")) { }
        else if (Has("先到这里", "先这样", "不聊了", "不想聊天", "晚安", "结束话题")) { act = "close"; primary = "close-stop"; }
        else if (Has("只是分享", "分享一下", "不求建议", "不用给建议", "不需要建议")) { act = "share"; primary = "share-no-solution"; }
        else if (Has("只是吐槽", "只是抱怨", "真烦", "烦死", "受够了", "无语")) { act = "listen"; primary = "emotion-vent"; tone = "soft"; }
        else if (Has("好累", "累了", "有点累", "疲惫", "没力气", "不想分析")) { act = "listen"; primary = "emotion-tired"; tone = "soft"; }
        else if (Has("实验失败", "实验又失败", "实验还是失败", "实验没成功")) { act = "respond"; primary = "technical-failed-experiment"; tone = "serious"; }
        else if (Has("我不适合", "我是不是不行", "是不是我不行", "我太差", "我做不好")) { act = "listen"; primary = "emotion-self-doubt"; tone = "soft"; }
        else if (Has("很失望", "有点失望", "真失望")) { act = "listen"; primary = "emotion-disappointment"; tone = "soft"; }
        else if (Has("太好了", "好开心", "终于成功", "很兴奋")) { act = "react"; primary = "emotion-excited"; }
        else if (Has("没想到", "居然这样", "真意外")) { act = "react"; primary = "reaction-surprise"; }
        else if (Has("觉得奇怪", "好奇怪")) { act = "react"; primary = "reaction-oddity"; }
        else if (Has("哈哈", "太好笑", "轻松一点")) { act = "react"; primary = "casual-light"; tone = "playful"; }
        else if (Has("核心矛盾", "矛盾在哪")) { act = "answer"; primary = "initiative-contrast"; depth = "brief"; }
        else if (Has("证据不够", "证据不足", "保留判断", "还不能确定")) { act = "answer"; primary = "uncertain-reserve"; }
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
        if (IsOpenConversation(text) && act is not ("answer" or "close" or "greet" or "acknowledge" or "continue"))
        {
            act = "initiative"; focus = "open_topic"; depth = "brief"; primary = "initiative-relevant";
            avoid.AddRange(["advice", "action_plan", "reassurance", "over_explanation", "service_offer", "preference_survey"]);
        }
        if (act is "share" or "listen" or "close" or "acknowledge" or "greet")
            avoid.AddRange(["advice", "action_plan", "follow_up_question", "over_explanation", "reassurance"]);
        if (act == "greet") avoid.Add("historical_task_resumption");
        if (act is "close" or "acknowledge" or "greet") depth = "minimal";
        var intent = context.ResponseIntent ?? new(act, "unspecified", focus, tone, depth,
            act == "initiative" ? "optional_topic" : "none", avoid);
        // A supplied Mind intent is authoritative. Expression cannot reinterpret its act/stance.
        if (context.ResponseIntent is not null)
            primary = intent.Stance switch { "uncertain" => "uncertain-evidence", "withheld" => "uncertain-reserve",
                "disagree" => "disagree-gentle", "agree" => "agree-simple", "partly_agree" => "agree-partial",
                _ => intent.Act switch { "clarify" => "clarify-one", "share" => "share-receive", "close" => "close-stop",
                    "continue" => "continue-direct", "initiative" => "initiative-relevant", "acknowledge" => "casual-ack", "react" => "reaction-quiet", _ => null } };
        if (intent.Act == "initiative" && intent.Avoid.Contains("topic_change")) primary = null;
        var ids = new List<string>();
        if (primary is not null) ids.Add(primary);
        if (primary is not null && intent.Avoid.Contains("follow_up_question") && intent.Act is "share" or "listen") ids.Add("share-space");
        return new(intent, Array.AsReadOnly(ids.Distinct(StringComparer.Ordinal).Take(2).Select(id => Patterns.Single(p => p.Id == id)).ToArray()));
    }

    internal static string? Build(PromptContext context, bool full)
    {
        if (context.ContentContext.ServantId != "mash_kyrielight" || context.ContentContext.PackageId != "official.mash") return null;
        var hint = "Expression Layer v0.2\n" + SemanticBoundary + "\n" + BasePolicy;
        if (!full) return hint;
        var plan = Select(context);
        return hint + "\nResponseIntent: " + JsonSerializer.Serialize(new { act = plan.Intent.Act, stance = plan.Intent.Stance,
            focus = plan.Intent.Focus, tone = plan.Intent.Tone, depth = plan.Intent.Depth, follow_up = plan.Intent.FollowUp, avoid = plan.Intent.Avoid })
            + (plan.Intent.Act == "initiative" && plan.Intent.Focus == "open_topic" && plan.Intent.FollowUp == "optional_topic"
                && !plan.Intent.Avoid.Contains("topic_change")
                ? "\n" + OpenConversationPolicy : string.Empty)
            + "\nSituational Patterns（0..2；Base 已足够自然就不选，不必用满；不适用时忽略，不能改变原意）：\n"
            + (plan.Patterns.Count == 0 ? "none" : string.Join("\n", plan.Patterns.Select(p => $"- {p.Id}: {p.Strategy}")));
    }
}
