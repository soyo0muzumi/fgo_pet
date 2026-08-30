using FgoPet.Core.Focus;

namespace FgoPet.Core.Events;

/// <summary>
/// Stable runtime event contract persisted to <c>runtime_events</c>. Types are
/// string constants from <see cref="RuntimeEventType"/>; timestamps are UTC;
/// IDs are stable business strings. Source metadata is optional so existing
/// Phase 2/3 events remain valid while external events can carry a sanitized
/// subject and summary.
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
    string? PayloadJson = null,
    string Source = RuntimeEventSource.System,
    string? SubjectId = null,
    string? Summary = null,
    bool IsPrivate = false);
