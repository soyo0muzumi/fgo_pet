namespace FgoPet.App.Dialogue;

public sealed record PromptBudget
{
    public PromptBudget(
        int ordinaryContextTokens = 2_500,
        int runtimeStateTokens = 250,
        int shortTermMemoryTokens = 600,
        int storyKnowledgeTokens = 900)
    {
        if (ordinaryContextTokens < 1 || runtimeStateTokens < 1 || shortTermMemoryTokens < 1 || storyKnowledgeTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinaryContextTokens));
        }

        OrdinaryContextTokens = ordinaryContextTokens;
        RuntimeStateTokens = runtimeStateTokens;
        ShortTermMemoryTokens = shortTermMemoryTokens;
        StoryKnowledgeTokens = storyKnowledgeTokens;
    }

    public int OrdinaryContextTokens { get; }
    public int RuntimeStateTokens { get; }
    public int ShortTermMemoryTokens { get; }
    public int StoryKnowledgeTokens { get; }
}
