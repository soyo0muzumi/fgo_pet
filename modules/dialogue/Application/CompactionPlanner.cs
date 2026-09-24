using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

public sealed record CompactionTurn(int FirstSequence, int LastSequence, int InputTokens, bool IsComplete);
public sealed record CompactableRange(int FirstSequence, int LastSequence);

public static class CompactionPlanner
{
    public static int TriggerTokens(PromptBudget budget) =>
        Math.Min((int)Math.Floor(budget.ContextWindowTokens * 0.8d), budget.InputTokens);

    public static CompactableRange? Select(IReadOnlyList<CompactionTurn> turns, int retainTokens)
    {
        var retainFrom = turns.Count;
        long retained = 0;
        var complete = 0;
        for (var index = turns.Count - 1; index >= 0 && (complete < 2 || retained < retainTokens); index--)
        {
            retained += turns[index].InputTokens;
            if (turns[index].IsComplete) complete++;
            retainFrom = index;
        }
        var end = -1;
        for (var index = 0; index < retainFrom; index++)
        {
            if (!turns[index].IsComplete || index > 0 && turns[index - 1].LastSequence + 1 != turns[index].FirstSequence) break;
            end = index;
        }
        return end < 0 ? null : new(turns[0].FirstSequence, turns[end].LastSequence);
    }
}
