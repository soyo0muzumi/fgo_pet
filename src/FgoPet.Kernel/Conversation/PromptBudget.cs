namespace FgoPet.Core.Dialogue;

public sealed record PromptBudget
{
    private PromptBudget() { }
    public int ContextWindowTokens { get; private init; }
    public int OutputTokens { get; private init; }
    public int SafetyMarginTokens { get; private init; }
    public int InputTokens { get; private init; }

    // DeepSeek Harness resolveCompactSpec: reserve output and headroom before accepting input.
    public static PromptBudget Resolve(ModelContextLimit limit, int requestedOutputTokens)
    {
        var margin = Math.Max(256L, (long)Math.Ceiling(limit.WindowTokens * 0.02d));
        var input = (long)limit.WindowTokens - requestedOutputTokens - margin;
        if (requestedOutputTokens <= 0 || limit.WindowTokens <= 0 || input <= 0 ||
            (limit.MaxOutputTokens is int max && requestedOutputTokens > max))
            throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
        return new PromptBudget { ContextWindowTokens = limit.WindowTokens,
            OutputTokens = requestedOutputTokens, SafetyMarginTokens = checked((int)margin), InputTokens = checked((int)input) };
    }
}
