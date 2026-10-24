namespace FgoPet.Extensibility;

/// <summary>User-owned source and resource choices, independent of saved interpretations.</summary>
public sealed record CompanionAwarenessOptions(
    bool ActivityContextEnabled = true,
    bool ApplicationsEnabled = false,
    bool BackgroundEnabled = false,
    int DailyRequestLimit = 6);

/// <summary>A host-owned, short-lived source choice. A token is never a window handle.</summary>
public sealed record CompanionAwarenessWindow(string Token, string Title, string Application);

public sealed record CompanionAwarenessStatus(
    string ContextToken,
    long Revision,
    bool Available,
    string ModelRecipient,
    bool ModelApprovalCurrent,
    CompanionAwarenessOptions Options,
    int RequestsUsed,
    IReadOnlyList<CompanionAwarenessWindow> Windows,
    string ActivityStatus,
    DateTimeOffset? LastActivityAtUtc = null);

/// <summary>The host derives scope and model identity. Web input cannot select either.</summary>
public interface ICompanionAwarenessControls
{
    CompanionAwarenessStatus GetStatus();
    ValueTask<CompanionAwarenessStatus> ConfigureAsync(string contextToken, long expectedRevision,
        CompanionAwarenessOptions options, CancellationToken token);
    ValueTask<IReadOnlyList<CompanionAwarenessWindow>> ListWindowsAsync(string contextToken, CancellationToken token);
    ValueTask<CompanionAwarenessStatus> AllowWindowAsync(string contextToken, long expectedRevision,
        string candidateToken, CancellationToken token);
    ValueTask<CompanionAwarenessStatus> ClearWindowsAsync(string contextToken, long expectedRevision,
        CancellationToken token);
}
