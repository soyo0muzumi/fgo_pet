using FgoPet.Core.Events;

namespace FgoPet.Core.Focus;

/// <summary>An event draft emitted by a transition; Task 2 promotes these to full persisted events.</summary>
public sealed record FocusEventDraft(
    string Type,
    string ServantId,
    int CycleNumber,
    FocusPhase Phase,
    int ElapsedSeconds,
    int EffectiveSeconds,
    DateTimeOffset OccurredAtUtc,
    string EventId);

/// <summary>Result of one deterministic transition: the next session plus emitted event drafts.</summary>
public sealed record FocusTransition(FocusSession Session, IReadOnlyList<FocusEventDraft> Events)
{
    public static FocusTransition WithoutEvents(FocusSession session) => new(session, Array.Empty<FocusEventDraft>());
}
