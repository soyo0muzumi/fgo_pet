namespace FgoPet.Extensibility;

public enum CompanionSignalKind { ActivityStarted, ActivityPaused, ActivityStopped, PhaseCompleted, ActivityCompleted }
public enum CompanionActivityPhase { Work, Rest }

/// <summary>A committed activity transition, never a display snapshot or renderer command.</summary>
public sealed record CompanionSignal(CompanionSignalKind Kind, string RoleId, string ActivityId,
    DateTimeOffset OccurredAtUtc, int Round, CompanionActivityPhase Phase, int ElapsedSeconds);

public interface ICompanionSignalSource
{
    event Action<CompanionSignal>? Signal;
}
