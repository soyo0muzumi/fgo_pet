namespace FgoPet.Platform.WindowText;

public static class WindowTextLimits
{
    public const int MaxTextChars = 6000;
    public const int MaxNodes = 400;
    public const int MaxDepth = 32;
    public const int MaxCandidates = 128;
    public static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(3);
}

/// <summary>The token is native-owned, ephemeral, and never a window handle or a Web input.</summary>
public sealed record WindowTextCandidate(string Token, string Title, string Application)
{
    public override string ToString() => $"{Application} — {Title}";
}

public enum WindowTextReadStatus { Complete, Partial, Unavailable, TimedOut, Busy }

public enum WindowTextReadStage { Requested, WorkerStarted, Completed, Cancelled, Deadline, WorkerReleased }

/// <summary>Safe boundary metadata only: never add source title, token, handle, application, or content.</summary>
public sealed record WindowTextReadDiagnostic(DateTimeOffset TimestampUtc, string RequestHash,
    WindowTextReadStage Stage, WindowTextReadStatus? Status, int CharacterCount);

public interface IWindowTextDiagnostics { void Record(WindowTextReadDiagnostic diagnostic); }

/// <summary>Local, in-memory snapshot. Never log or persist its text.</summary>
public sealed record WindowTextSnapshot(string SourceTitle, string Application, string Text,
    DateTimeOffset ReadAtUtc, WindowTextReadStatus Status)
{
    public bool CanAttach => Status is WindowTextReadStatus.Complete or WindowTextReadStatus.Partial
        && !string.IsNullOrWhiteSpace(Text);
    public override string ToString() => $"WindowTextSnapshot({Status}, {Text.Length} characters)";
}

public interface IWindowTextReader
{
    IReadOnlyList<WindowTextCandidate> ListCandidates();
    Task<WindowTextSnapshot> ReadAsync(string candidateToken, CancellationToken cancellationToken);
}
