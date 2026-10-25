namespace FgoPet.Core.Dialogue;

/// <summary>Summary generation reserves its own output while retaining provider input constraints.</summary>
public static class SummaryBudgetPolicy
{
    public static PromptBudget Resolve(PromptBudget replyBudget)
    {
        ArgumentNullException.ThrowIfNull(replyBudget);
        var desired = Math.Clamp(replyBudget.ContextWindowTokens / 16, 512, 16384);
        var available = replyBudget.ContextWindowTokens - replyBudget.SafetyMarginTokens;
        var output = Math.Min(desired, Math.Max(1, available / 2));
        if (replyBudget.ModelMaxOutputTokens is int maximum)
            output = Math.Min(output, maximum);
        // Re-reserving on the existing budget must not discard IChatInputLimit/ConstrainInput.
        return replyBudget.WithOutputReservation(output);
    }
}
