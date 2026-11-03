using System.Text.Json;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

internal enum ConversationMomentum { Stop, Hold, Continue, Initiate }
internal enum ConversationContribution { React, Observe }
internal enum ConversationQuestionPolicy { None, Clarify, Open }

/// <summary>Ephemeral realization control derived from resolved intent, not another semantic classifier.</summary>
internal sealed record ConversationControl(ConversationMomentum Momentum, ConversationContribution Contribution, ConversationQuestionPolicy QuestionPolicy)
{
    internal static ConversationControl Resolve(ResponseIntent intent, IReadOnlyList<SituationalExpression> patterns,
        bool inheritedFrame, TurnUnderstanding? understanding = null)
    {
        var noQuestion = intent.Avoid.Contains("follow_up_question");
        // Semantic reading wins over lexical inference when it is available. It only reorders the
        // same three axes; it never grants a question, a topic change or a factual claim.
        if (understanding is { } turn && !turn.IsUnknown)
        {
            var semanticQuestion = noQuestion || intent.FollowUp != "optional_topic"
                ? ConversationQuestionPolicy.None : ConversationQuestionPolicy.Open;
            switch (turn.Relation)
            {
                case TurnRelation.Close:
                    return new(ConversationMomentum.Stop, ConversationContribution.React, ConversationQuestionPolicy.None);
                case TurnRelation.Request when turn.Request == TurnRequest.NeedsAnswer:
                    // An explicit request is answered in full; brevity must not swallow its content.
                    return new(ConversationMomentum.Continue, ConversationContribution.Observe, semanticQuestion);
                case TurnRelation.Respond when turn.InformationDelta == InformationDelta.None:
                    // Pure acknowledgement, in context: stay on the point and leave one concrete hook.
                    return new(ConversationMomentum.Hold, ConversationContribution.React, ConversationQuestionPolicy.None);
                case TurnRelation.Respond:
                case TurnRelation.Extend:
                    return new(ConversationMomentum.Continue, ConversationContribution.Observe, semanticQuestion);
                case TurnRelation.Redirect:
                case TurnRelation.Request:
                    return new(ConversationMomentum.Continue, ConversationContribution.Observe, semanticQuestion);
            }
        }
        if (intent.Act is "close" or "greet" or "acknowledge") return new(ConversationMomentum.Stop, ConversationContribution.React, ConversationQuestionPolicy.None);
        // Necessary task clarification is independent of optional conversational extension.
        if (intent.Act == "clarify") return new(ConversationMomentum.Continue, ConversationContribution.Observe, ConversationQuestionPolicy.Clarify);
        if (intent.FollowUp == "none") return new(ConversationMomentum.Stop, ConversationContribution.Observe, ConversationQuestionPolicy.None);
        if (intent.Act == "listen" || intent.Act == "continue" && intent.Tone == "soft" && patterns.Any(p => p.Category == "Emotional"))
            return new(ConversationMomentum.Hold, inheritedFrame || intent.Act == "continue" ? ConversationContribution.Observe : ConversationContribution.React, ConversationQuestionPolicy.None);
        if (intent.Act == "initiative" && intent.Focus == "open_topic" && !intent.Avoid.Contains("topic_change"))
            return new(ConversationMomentum.Initiate, ConversationContribution.Observe, noQuestion ? ConversationQuestionPolicy.None : ConversationQuestionPolicy.Open);
        return new(ConversationMomentum.Continue, !inheritedFrame && (intent.Act == "react" || patterns.Any(p => p.Id == "share-good-news"))
                ? ConversationContribution.React : ConversationContribution.Observe,
            !noQuestion && intent.FollowUp == "optional_topic" ? ConversationQuestionPolicy.Open : ConversationQuestionPolicy.None);
    }

    internal string BuildHint()
    {
        var momentum = Momentum switch {
            ConversationMomentum.Stop => "自然简短收住当前回合；不延伸新话题，也不主动恢复旧任务。",
            ConversationMomentum.Hold => "停留当前处境，允许有内容，不主动转向、积极重构或安排下一步。",
            ConversationMomentum.Continue => "沿当前话题增加一个小切面，不重新开场。",
            _ => "在用户允许的开放空间带出一个具体话题。" };
        var contribution = Contribution == ConversationContribution.React
            ? "直接回应当前表达，不必新增事实。" : "贡献当前话题的一个具体观察、反差或类比。";
        var question = QuestionPolicy switch {
            ConversationQuestionPolicy.None => "本轮不主动索取新的聊天信息，用陈述回应或贡献内容。",
            ConversationQuestionPolicy.Clarify => "仅在完成当前用户任务确实缺少必要输入时，优先单点澄清真正的信息缺口；已有足够信息就直接回应。",
            _ => "本轮允许围绕当前具体点自然探索提问，也可以只陈述，不必提问。" };
        return "\nConversationControl: " + JsonSerializer.Serialize(new {
            momentum = Momentum.ToString().ToLowerInvariant(), contribution = Contribution.ToString().ToLowerInvariant(),
            questionPolicy = QuestionPolicy.ToString().ToLowerInvariant() })
            + "\nQuestionPolicy 统一决定可选聊天提问权限；follow_up 与其他表达策略不单独赋予权限。None 只限制可选聊天问题；Replyer 发现完成当前任务确实缺少必要输入时仍须按 Clarify 单点澄清，不得猜答。任务执行、能力与工具/权限约束仍由 Replyer、ResponseIntent 和宿主负责。观点与立场仍由 ResponseIntent.Stance、Mind 和 Persona 决定。"
            + momentum + contribution + question;
    }
}
