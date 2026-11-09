namespace FgoPet.Dialogue;

/// <summary>Process-level switch for continuation-aware recall in ordinary and Native conversations.</summary>
public static class ConversationRecallContinuationGate
{
    /// <summary>Maximum number of already-loaded user turns used as continuation evidence.</summary>
    public const int MaxRecentTurns = 3;

    private static volatile bool _enabled = true;

    /// <summary>Disable to use explicit historical references as the only automatic recall entry point.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
