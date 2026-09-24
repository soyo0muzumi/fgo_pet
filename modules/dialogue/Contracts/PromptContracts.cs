using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Validation;

namespace FgoPet.Core.Dialogue;

public static class PromptContracts
{
    public const int MaxRuntimeStateChars = 250;
    public const int MaxPendingTodoDraftChars = 12_000;
    public const int MaxMessageChars = 12_000;
}

public enum PromptBudgetFailure { PendingDraftTooLarge, InsufficientContext, UserInputTooLarge }

public sealed class PromptBudgetException(PromptBudgetFailure failure) : Exception("Prompt context exceeds its bounded budget.")
{
    public PromptBudgetFailure Failure { get; } = failure;
}

public sealed record PromptMessage
{
    public PromptMessage(ChatMessageRole role, string text)
    {
        Role = role;
        Text = Phase3Validation.Text(text, nameof(text), PromptContracts.MaxMessageChars);
    }

    public ChatMessageRole Role { get; }
    public string Text { get; }
}

/// <summary>
/// Safe, transient context selected for one dialogue request. It contains display
/// names and opaque identifiers only; local file paths never belong in this contract.
/// </summary>
public sealed record ConversationRequestContext
{
    public ConversationRequestContext(
        string? projectId = null,
        string? projectLabel = null,
        IEnumerable<string>? attachmentNames = null,
        string? intentId = null,
        string? intentLabel = null)
    {
        ProjectId = OptionalId(projectId, nameof(projectId));
        ProjectLabel = DisplayName(projectLabel, nameof(projectLabel));
        AttachmentNames = (attachmentNames ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => DisplayName(name, nameof(attachmentNames)))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        IntentId = OptionalId(intentId, nameof(intentId), 64);
        IntentLabel = DisplayName(intentLabel, nameof(intentLabel));
    }

    public string ProjectId { get; }
    public string ProjectLabel { get; }
    public IReadOnlyList<string> AttachmentNames { get; }
    public string IntentId { get; }
    public string IntentLabel { get; }

    public bool IsEmpty => ProjectId.Length == 0
        && ProjectLabel.Length == 0
        && AttachmentNames.Count == 0
        && IntentId.Length == 0
        && IntentLabel.Length == 0;

    private static string OptionalId(string? value, string parameterName, int maxLength = 128) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Phase3Validation.Id(value, parameterName, maxLength);

    private static string DisplayName(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        if (separator >= 0)
        {
            normalized = normalized[(separator + 1)..];
        }

        return normalized.Length == 0
            ? string.Empty
            : Phase3Validation.Text(normalized, parameterName, 160);
    }
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
        string userMessage,
        ConversationRequestContext? requestContext = null,
        string? pendingTodoDraft = null,
        IReadOnlyList<HistoryHit>? recalledHistory = null,
        RecallStatus recallStatus = RecallStatus.Empty,
        ConversationSummary? conversationSummary = null)
    {
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Persona = persona ?? throw new ArgumentNullException(nameof(persona));
        Knowledge = knowledge is null ? throw new ArgumentNullException(nameof(knowledge)) : knowledge.ToArray();
        Memories = memories is null ? throw new ArgumentNullException(nameof(memories)) : memories.ToArray();
        RuntimeState = Phase3Validation.OptionalText(runtimeState, nameof(runtimeState), PromptContracts.MaxRuntimeStateChars);
        if (pendingTodoDraft?.Length > PromptContracts.MaxPendingTodoDraftChars)
            throw new PromptBudgetException(PromptBudgetFailure.PendingDraftTooLarge);
        PendingTodoDraft = pendingTodoDraft ?? string.Empty;
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        UserMessage = Phase3Validation.Text(userMessage, nameof(userMessage), 12_000);
        RequestContext = requestContext ?? new ConversationRequestContext();
        RecalledHistory = recalledHistory?.ToArray() ?? [];
        RecallStatus = recallStatus;
        ConversationSummary = conversationSummary;
    }

    public ContentContextKey ContentContext { get; }
    public PersonaBundle Persona { get; }
    public IReadOnlyList<KnowledgeEntry> Knowledge { get; }
    public IReadOnlyList<StoredMemory> Memories { get; }
    public string RuntimeState { get; }
    public string PendingTodoDraft { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public string UserMessage { get; }
    public ConversationRequestContext RequestContext { get; }
    public IReadOnlyList<HistoryHit> RecalledHistory { get; }
    public RecallStatus RecallStatus { get; }
    public ConversationSummary? ConversationSummary { get; }
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
        TokenMeasurement usage,
        PromptAssemblyStatus status,
        int maxOutputTokens,
        bool fitsBudget)
    {
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        Usage = usage;
        Status = status;
        MaxOutputTokens = maxOutputTokens;
        FitsBudget = fitsBudget;
    }

    public ContentContextKey ContentContext { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public TokenMeasurement Usage { get; }
    public int EstimatedTokens => Usage.InputTokens;
    public int MaxOutputTokens { get; }
    public bool FitsBudget { get; }
    public PromptAssemblyStatus Status { get; }
}
