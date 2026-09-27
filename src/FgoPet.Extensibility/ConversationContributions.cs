namespace FgoPet.Extensibility;

public static class ConversationContributionLimits
{
    public const int RuntimeContextChars = 250;
    public const int ReplyChars = 12_000;
    public const int PendingStateChars = 12_000;
}

public enum CapabilityOutcome
{
    None, ProposalsReady, InvalidToolCall, NoProposal, TextFallback, PendingDraftReplaced,
    ConfirmationUnknown, Cancelled, Confirmed, CommitUnknown
}

/// <summary>Capability-owned state stays opaque; conversation persists the reply and carries navigation metadata.</summary>
public sealed record ConversationContributionResult(CapabilityOutcome Outcome, string? Reply = null,
    string? Detail = null, string? StructuredPayload = null, string? DraftId = null, int? DraftVersion = null,
    string? CreatedItemId = null, string? WorkspaceId = null);

public interface IConversationContextProvider
{
    string BuildContext(ToolScope scope, string userMessage);
}

/// <summary>Called inside the conversation owner's commit fence. Interpretation never grants generic command execution.</summary>
public interface IConversationContinuationProvider
{
    ConversationContributionResult? TryHandleInput(ToolScope scope, string userMessage);
    string? GetPromptState(ToolScope scope);
    void ClearRole(string roleId);
    void RemoveConversation(ToolScope scope);
}

public interface ITextReplyInterpreter
{
    ConversationContributionResult? TryInterpretReply(ToolScope scope, string reply);
}

public sealed record RegisteredContribution<T>(string PluginId, T Provider);

public enum ConversationPromptBlockKind { Data, Instruction }
public sealed record ConversationPromptBlock(string Source, string Text,
    ConversationPromptBlockKind Kind = ConversationPromptBlockKind.Data, bool Whole = true);
public interface IConversationPromptProvider
{
    IReadOnlyList<ConversationPromptBlock> BuildPrompt(ToolScope scope, string userMessage, bool toolsAvailable);
}

public enum ConversationSourceKind { User, Assistant }
public sealed record ConversationSourceSnapshot(ToolScope Scope, string MessageId, string Fingerprint,
    DateTimeOffset OccurredAtUtc, ConversationSourceKind Kind, string? ProjectLabel);
public interface IConversationSourceReader
{
    ConversationSourceSnapshot Read(ToolScope scope, string messageId, ConversationSourceKind kind);
    bool IsCurrent(ConversationSourceSnapshot source);
}

public sealed record ConversationCompletedTurn(ToolScope Scope, string UserMessageId, string UserText,
    string AssistantMessageId, string AssistantText, string? SuggestedFact,
    FgoPet.Core.Settings.ModelConnectionSettings? Connection, CancellationToken CancellationToken);
public interface IPostTurnObserver
{
    void Observe(ConversationCompletedTurn turn);
}
