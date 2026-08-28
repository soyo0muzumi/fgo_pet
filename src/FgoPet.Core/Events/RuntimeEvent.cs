using FgoPet.Core.Focus;

namespace FgoPet.Core.Events;

/// <summary>
/// Stable runtime event contract persisted to <c>runtime_events</c>. Types are
/// string constants from <see cref="RuntimeEventType"/>; booleans are not present;
/// timestamps are UTC; IDs are stable business strings.
/// </summary>
public sealed record RuntimeEvent(
    string EventId,
    string SessionId,
    string Type,
    DateTimeOffset OccurredAtUtc,
    int CycleNumber,
    FocusPhase Phase,
    string ServantId,
    int ElapsedSeconds,
    int EffectiveSeconds,
    int Priority,
    int SchemaVersion = 1,
    string? PayloadJson = null);
