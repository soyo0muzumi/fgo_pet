using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

internal enum ConversationMomentum { Stop, Hold, Continue, Initiate }
internal enum ConversationContribution { React, Observe }
internal enum ConversationQuestionPolicy { None, Clarify, Open }

/// <summary>Ephemeral realization control derived from resolved intent, not another semantic classifier.</summary>
internal sealed record ConversationControl(ConversationMomentum Momentum, ConversationContribution Contribution, ConversationQuestionPolicy QuestionPolicy)
{
    private static bool IsSustainedDiscussion(IReadOnlyList<SituationalExpression> patterns) =>
        patterns.Any(p => p.Id is "technical-architecture" or "technical-direction"
            or "uncertain-reserve" or "uncertain-evidence" or "uncertain-hypothesis" or "uncertain-provisional"
            or "share-detail");

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
        if (intent.Act == "answer" && intent.Focus == "current_request") return new(ConversationMomentum.Stop, ConversationContribution.Observe, ConversationQuestionPolicy.None);
        if (IsSustainedDiscussion(patterns) && !patterns.Any(p => p.Id == "share-space"))
            return new(ConversationMomentum.Hold, ConversationContribution.Observe, ConversationQuestionPolicy.None);
        if (intent.FollowUp == "none") return new(ConversationMomentum.Stop, ConversationContribution.Observe, ConversationQuestionPolicy.None);
        if (intent.Act == "listen" || intent.Act == "continue" && intent.Tone == "soft" && patterns.Any(p => p.Category == "Emotional"))
            return new(ConversationMomentum.Hold, inheritedFrame || intent.Act == "continue" ? ConversationContribution.Observe : ConversationContribution.React, ConversationQuestionPolicy.None);
        if (intent.Act == "initiative" && intent.Focus == "open_topic" && !intent.Avoid.Contains("topic_change"))
            return new(ConversationMomentum.Initiate, ConversationContribution.Observe, noQuestion ? ConversationQuestionPolicy.None : ConversationQuestionPolicy.Open);
        return new(ConversationMomentum.Continue, !inheritedFrame && (intent.Act == "react" || patterns.Any(p => p.Id == "share-good-news"))
                ? ConversationContribution.React : ConversationContribution.Observe,
            !noQuestion && intent.FollowUp == "optional_topic" ? ConversationQuestionPolicy.Open : ConversationQuestionPolicy.None);
    }

}
