namespace FgoPet.Core.Bond;

/// <summary>Evaluated bond level and progress for one servant.</summary>
public sealed record BondProgress(
    int Level,
    long LifetimeFocusSeconds,
    long CurrentThresholdSeconds,
    long NextThresholdSeconds,
    bool IsMaxLevel);
