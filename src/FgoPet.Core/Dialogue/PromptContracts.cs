using FgoPet.Core.Memory;
using FgoPet.Core.Packs;

namespace FgoPet.Core.Dialogue;

public static class PromptContracts
{
    public const int MaxRuntimeStateChars = 250;
}

public sealed record PromptMessage
{
    public PromptMessage(ChatMessageRole role, string text)
    {
        Role = role;
        Text = Phase3Validation.Text(text, nameof(text), 12_000);
    }

    public ChatMessageRole Role { get; }
    public string Text { get; }
}

public sealed record PromptContext
{
    public PromptContext(
        ContentContextKey contentContext,
        PersonaBundle persona,
        IReadOnlyList<KnowledgeEntry> knowledge,
        IReadOnlyList<StoredMemory> memories,
        string runtimeState,
        IReadOnlyList<PromptMessage> messages,
        string userMessage)
    {
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Persona = persona ?? throw new ArgumentNullException(nameof(persona));
        Knowledge = knowledge is null ? throw new ArgumentNullException(nameof(knowledge)) : knowledge.ToArray();
        Memories = memories is null ? throw new ArgumentNullException(nameof(memories)) : memories.ToArray();
        RuntimeState = Phase3Validation.OptionalText(runtimeState, nameof(runtimeState), PromptContracts.MaxRuntimeStateChars);
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        UserMessage = Phase3Validation.Text(userMessage, nameof(userMessage), 12_000);
    }

    public ContentContextKey ContentContext { get; }
    public PersonaBundle Persona { get; }
    public IReadOnlyList<KnowledgeEntry> Knowledge { get; }
    public IReadOnlyList<StoredMemory> Memories { get; }
    public string RuntimeState { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public string UserMessage { get; }
}

public enum PromptAssemblyStatus
{
    Complete,
    Truncated,
    ContentUnavailable,
}

public sealed record ComposedPrompt
{
    public ComposedPrompt(
        ContentContextKey contentContext,
        IReadOnlyList<PromptMessage> messages,
        int estimatedTokens,
        PromptAssemblyStatus status)
    {
        if (estimatedTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedTokens));
        }

        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        EstimatedTokens = estimatedTokens;
        Status = status;
    }

    public ContentContextKey ContentContext { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public int EstimatedTokens { get; }
    public PromptAssemblyStatus Status { get; }
}
