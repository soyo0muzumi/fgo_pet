namespace FgoPet.Core.Dialogue;

public sealed record PromptBudget
{
    private PromptBudget() { }
    public int ContextWindowTokens { get; private init; }
    public int OutputTokens { get; private init; }
    public int SafetyMarginTokens { get; private init; }
    public int InputTokens { get; private init; }
    public int? ModelMaxOutputTokens { get; private init; }
    public PromptBudget WithOutputReservation(int outputTokens)
    {
        var input = (long)ContextWindowTokens - outputTokens - SafetyMarginTokens;
        if (outputTokens <= 0 || input <= 0 || ModelMaxOutputTokens is int max && outputTokens > max)
            throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
        // Preserve any provider/task input constraint while reserving auxiliary output.
        return this with { OutputTokens = outputTokens, InputTokens = Math.Min(InputTokens, checked((int)input)) };
    }
    public PromptBudget ConstrainInput(int maximum)
    {
        if(maximum<=0)throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
        return this with{InputTokens=Math.Min(InputTokens,maximum)};
    }

    // DeepSeek Harness resolveCompactSpec: reserve output and headroom before accepting input.
    public static PromptBudget Resolve(ModelContextLimit limit, int requestedOutputTokens)
    {
        var margin = Math.Max(256L, (long)Math.Ceiling(limit.WindowTokens * 0.02d));
        var input = (long)limit.WindowTokens - requestedOutputTokens - margin;
        if (requestedOutputTokens <= 0 || limit.WindowTokens <= 0 || input <= 0 ||
            (limit.MaxOutputTokens is int max && requestedOutputTokens > max))
            throw new PromptBudgetException(PromptBudgetFailure.InsufficientContext);
        return new PromptBudget { ContextWindowTokens = limit.WindowTokens,
            OutputTokens = requestedOutputTokens, SafetyMarginTokens = checked((int)margin), InputTokens = checked((int)input),
            ModelMaxOutputTokens = limit.MaxOutputTokens };
    }
}
