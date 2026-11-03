using FgoPet.Extensibility;
using FgoPet.Core.Packs;
using FgoPet.Core.Validation;

namespace FgoPet.Core.Dialogue;

public static class PromptContracts
{
    public const int MaxRuntimeStateChars = 250;
    public const int MaxPendingStateChars = 12_000;
    public const int MaxMessageChars = 12_000;
}

public enum PromptBudgetFailure { PendingDraftTooLarge, InsufficientContext, UserInputTooLarge, SelectedWindowTooLarge }

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
/// Transient context selected for one dialogue request. Selected window text is
/// external untrusted material; it must not become a user instruction or memory.
/// </summary>
public sealed record ConversationRequestContext
{
    public ConversationRequestContext(
        string? projectId = null,
        string? projectLabel = null,
        IEnumerable<string>? attachmentNames = null,
        string? intentId = null,
        string? intentLabel = null,
        SelectedWindowContent? selectedWindowContent = null)
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
        SelectedWindowContent = selectedWindowContent;
    }

    public string ProjectId { get; }
    public string ProjectLabel { get; }
    public IReadOnlyList<string> AttachmentNames { get; }
    public string IntentId { get; }
    public string IntentLabel { get; }
    public SelectedWindowContent? SelectedWindowContent { get; }

    public bool IsEmpty => ProjectId.Length == 0
        && ProjectLabel.Length == 0
        && AttachmentNames.Count == 0
        && IntentId.Length == 0
        && IntentLabel.Length == 0
        && SelectedWindowContent is null;

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

/// <summary>One explicitly previewed window snapshot, sent only with the next admitted desktop request.</summary>
public sealed record SelectedWindowContent
{
    public SelectedWindowContent(string sourceTitle, string application, string text, DateTimeOffset readAtUtc, bool isPartial)
    {
        SourceTitle = Phase3Validation.Text(sourceTitle, nameof(sourceTitle), 160);
        Application = Phase3Validation.Text(application, nameof(application), 160);
        Text = Phase3Validation.Text(text, nameof(text), FgoPet.Platform.WindowText.WindowTextLimits.MaxTextChars);
        ReadAtUtc = readAtUtc.ToUniversalTime();
        IsPartial = isPartial;
    }
    public string SourceTitle { get; }
    public string Application { get; }
    public string Text { get; }
    public DateTimeOffset ReadAtUtc { get; }
    public bool IsPartial { get; }
    public override string ToString() => $"SelectedWindowContent({Text.Length} characters, partial={IsPartial})";
}
public sealed record PromptContext
{
    public PromptContext(
        ContentContextKey contentContext,
        PersonaBundle persona,
        IReadOnlyList<KnowledgeEntry> knowledge,
        IReadOnlyList<ConversationPromptBlock> contributions,
        string runtimeState,
        IReadOnlyList<PromptMessage> messages,
        string userMessage,
        ConversationRequestContext? requestContext = null,
        string? pendingCapabilityState = null,
        IReadOnlyList<HistoryHit>? recalledHistory = null,
        RecallStatus recallStatus = RecallStatus.Empty,
        ConversationSummary? conversationSummary = null)
    {
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Persona = persona ?? throw new ArgumentNullException(nameof(persona));
        Knowledge = knowledge is null ? throw new ArgumentNullException(nameof(knowledge)) : knowledge.ToArray();
        Contributions = contributions is null ? throw new ArgumentNullException(nameof(contributions)) : contributions.ToArray();
        RuntimeState = Phase3Validation.OptionalText(runtimeState, nameof(runtimeState), PromptContracts.MaxRuntimeStateChars);
        if (pendingCapabilityState?.Length > PromptContracts.MaxPendingStateChars)
            throw new PromptBudgetException(PromptBudgetFailure.PendingDraftTooLarge);
        PendingCapabilityState = pendingCapabilityState ?? string.Empty;
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
    public IReadOnlyList<ConversationPromptBlock> Contributions { get; }
    public string RuntimeState { get; }
    public string PendingCapabilityState { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public string UserMessage { get; }
    public ConversationRequestContext RequestContext { get; }
    public IReadOnlyList<HistoryHit> RecalledHistory { get; }
    public RecallStatus RecallStatus { get; }
    public ConversationSummary? ConversationSummary { get; }
    public ResponseIntent? ResponseIntent { get; init; }
    public RecallPolicyState RecallPolicyState { get; init; } = RecallPolicyState.None;
    public PromptPhase? Phase { get; init; }
}

[Flags]
public enum RecallPolicyState
{
    None = 0,
    PriorClarificationUnresolved = 1 << 0,
    CandidateConflict = 1 << 1,
    UserChallengedRecall = 1 << 2,
}

public enum PromptPlacement
{
    HostPrimacy = 0,
    Identity = 10,
    ConversationPolicy = 20,
    CapabilityPolicy = 30,
    ContextEvidence = 40,
    LatePolicy = 50,
    TurnTail = 60,
}

[Flags]
public enum PromptPhase
{
    None = 0,
    OrdinaryChat = 1 << 0,
    OrdinaryToolStep = 1 << 1,
    NativeToolStep = 1 << 2,
    NativeFinalOutput = 1 << 3,

    AllOrdinary = OrdinaryChat | OrdinaryToolStep,
    AllNative = NativeToolStep | NativeFinalOutput,
    All = AllOrdinary | AllNative,
}

public enum PhaseRequirement
{
    Disabled = 0,
    Optional = 1,
    Compact = 2,
    Required = 3,
}

public enum PromptBudgetTier
{
    Core = 0,
    ExplicitAttachment = 10,
    TaskRelevant = 20,
    Supporting = 30,
    Enhancement = 40,
}

public enum PromptEvictionPolicy
{
    Never = 0,
    PrefixTruncate = 1,
    WholeDrop = 2,
    SoftDrop = 3,
}

public enum PromptAuthority
{
    Host = 0,
    Capability = 1,
    Persona = 2,
    User = 3,
    Evidence = 4,
}

public enum PromptDataTrust
{
    TrustedSystemText = 0,
    PersonaEnvelope = 1,
    CurrentUserEnvelope = 2,
    UntrustedDataEnvelope = 3,
    UntrustedChunkedData = 4,
}

public sealed record PromptBlock(
    string Id,
    string Source,
    string Owner,
    PromptPlacement Placement,
    int OrderWithinPlacement,
    PromptPhase ActivePhases,
    PhaseRequirement BaseRequirement,
    PromptBudgetTier BudgetTier,
    int BudgetPriority,
    PromptEvictionPolicy EvictionPolicy,
    PromptAuthority Authority,
    PromptDataTrust DataTrust,
    string Content,
    ChatMessageRole Role = ChatMessageRole.System,
    PromptBudgetFailure OverflowFailure = PromptBudgetFailure.InsufficientContext,
    PromptPhase CompactPhases = PromptPhase.None,
    string? CompactContent = null,
    bool IsStateActive = true)
{
    public PhaseRequirement ResolveRequirement(PromptPhase phase) =>
        (ActivePhases & phase) == 0 ? PhaseRequirement.Disabled :
        (CompactPhases & phase) != 0 ? PhaseRequirement.Compact :
        BaseRequirement;

    public string ResolveContent(PromptPhase phase) =>
        (CompactPhases & phase) != 0 && !string.IsNullOrWhiteSpace(CompactContent)
            ? CompactContent
            : Content;
}

public enum PromptBlockOutcome
{
    Included,
    Compacted,
    Truncated,
    DroppedByPhase,
    DroppedByState,
    DroppedByBudget,
}

public sealed record PromptBlockTrace(
    string Id,
    string Source,
    PromptPlacement Placement,
    PromptBudgetTier Tier,
    PhaseRequirement Requirement,
    PromptBlockOutcome Outcome,
    int OriginalChars,
    int IncludedChars,
    int EstimatedTokenDelta);

public sealed record PromptManifest(
    PromptPhase Phase,
    IReadOnlyList<PromptBlockTrace> Blocks,
    int TotalInputTokens,
    int InputTokenBudget,
    bool FitsBudget);

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
        bool fitsBudget,
        PromptManifest? manifest = null)
    {
        ContentContext = contentContext ?? throw new ArgumentNullException(nameof(contentContext));
        Messages = messages is null ? throw new ArgumentNullException(nameof(messages)) : messages.ToArray();
        Usage = usage;
        Status = status;
        MaxOutputTokens = maxOutputTokens;
        FitsBudget = fitsBudget;
        Manifest = manifest;
    }

    public ContentContextKey ContentContext { get; }
    public IReadOnlyList<PromptMessage> Messages { get; }
    public TokenMeasurement Usage { get; }
    public int EstimatedTokens => Usage.InputTokens;
    public int MaxOutputTokens { get; }
    public bool FitsBudget { get; }
    public PromptAssemblyStatus Status { get; }
    public PromptManifest? Manifest { get; }
}
