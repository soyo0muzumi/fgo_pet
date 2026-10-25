namespace FgoPet.Extensibility;

/// <summary>A bounded observation supplied by its owner. Content is reference data, never instructions or authority.</summary>
public sealed record CompanionObservation(string SourceId, string Version, DateTimeOffset ObservedAtUtc,
    DateTimeOffset ValidUntilUtc, string Content, bool IsInterpretation = false);

/// <summary>Revision changes with meaningful source state, not with each clock sample.</summary>
public sealed record CompanionContextSnapshot(ToolScope Scope, string Revision,
    IReadOnlyList<CompanionObservation> Observations);
