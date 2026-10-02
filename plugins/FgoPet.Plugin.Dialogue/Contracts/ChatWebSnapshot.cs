namespace FgoPet.Dialogue.Contracts;

public sealed record ChatWebIdentity(string SessionId, string ServantId, string ConversationId);

public sealed record ChatWebDraftSnapshot(string Text, long Revision);

public sealed record ChatWebProjectContext(string Id, string Label);

public sealed record ChatWebContextChip(string Id, string Label);

public sealed record ChatWebSource(string Id, string Title, string Detail);

public sealed record ChatWebTurnSnapshot(
    string Id,
    string Role,
    string Text,
    bool IsStreaming,
    string ReasoningText,
    string ReasoningSummary,
    string ReasoningDurationText,
    bool IsThinkingActive,
    bool IsReasoningExpanded,
    bool CanCopy,
    bool CanReadAloud,
    bool CanOpenWorkspace,
    bool SpeechBusy,
    string SpeechActionText,
    string SpeechStatusText,
    bool SpeechNeedsConfiguration);

public sealed record ChatWebCapabilityNotice(string Text, string Kind);

public sealed record ChatWebConversationSnapshot(
    IReadOnlyList<ChatWebTurnSnapshot> Turns,
    bool IsStreaming,
    bool IsThinking,
    string ThinkingTimerText,
    string RequestStatusText,
    string ErrorText,
    bool ConfigurationRequired,
    string ProviderStatusText,
    string ModelStatusText,
    bool CanOpenModelSettings,
    bool CanSend,
    bool CanStop,
    bool CanRetryCapability,
    bool ShowReasoning,
    ChatWebCapabilityNotice CapabilityNotice,
    ChatWebProjectContext Project,
    IReadOnlyList<ChatWebContextChip> ContextChips,
    IReadOnlyList<ChatWebSource> RecalledSources);

public sealed record ChatWebHistoryItem(
    string ConversationId,
    string Title,
    string UpdatedText,
    string Status);

public sealed record ChatWebHistorySnapshot(
    IReadOnlyList<ChatWebHistoryItem> Items,
    string Status,
    bool IsLoading,
    bool HasMore,
    bool OnlyCurrentProject,
    string? PendingDeleteConversationId,
    string? PendingDeleteTitle,
    bool CanDelete);

public sealed record ChatWebSnapshot(
    ChatWebIdentity Identity,
    long Version,
    ChatWebDraftSnapshot Draft,
    ChatWebHostPresentation Presentation,
    ChatWebConversationSnapshot Conversation,
    ChatWebHistorySnapshot History);
