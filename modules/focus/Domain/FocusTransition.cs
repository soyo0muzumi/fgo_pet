using FgoPet.Core.Events;

namespace FgoPet.Core.Focus;

/// <summary>An event draft emitted by a transition.</summary>
public sealed record FocusEventDraft(
    string Type,
    string ServantId,
    int CycleNumber,
    FocusPhase Phase,
    int ElapsedSeconds,
    int EffectiveSeconds,
    DateTimeOffset OccurredAtUtc,
    string EventId)
{
    /// <summary>Promotes the draft to a persisted event using injected deterministic IDs.</summary>
    public RuntimeEvent ToRuntimeEvent(string sessionId, int priority, int schemaVersion = 1, string? payloadJson = null) =>
        new(EventId, sessionId, Type, OccurredAtUtc, CycleNumber, Phase, ServantId,
            ElapsedSeconds, EffectiveSeconds, priority, schemaVersion, payloadJson);
}

/// <summary>Result of one deterministic transition: the next session plus emitted event drafts.</summary>
public sealed record FocusTransition(FocusSession Session, IReadOnlyList<FocusEventDraft> Events)
{
    public static FocusTransition WithoutEvents(FocusSession session) => new(session, Array.Empty<FocusEventDraft>());
}
