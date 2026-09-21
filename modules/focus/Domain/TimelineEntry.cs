namespace FgoPet.Core.Timeline;

/// <summary>Minimal read-only timeline projection shown in the Today column.</summary>
public sealed record TimelineEntry(
    string EntryId,
    string SourceEventId,
    DateTimeOffset OccurredAtUtc,
    string Type,
    string ServantId,
    int ElapsedSeconds,
    int EffectiveSeconds,
    int? BondLevel);
