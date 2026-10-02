namespace FgoPet.App.Bootstrap;

/// <summary>Runtime database schema migration boundary (testable).</summary>
public interface IRuntimeDatabaseMigrator
{
    void Migrate();
}

/// <summary>Focus session recovery boundary (testable).</summary>
public interface IFocusRestorer
{
    void Restore();
}

/// <summary>Tracks whether the Phase 2 runtime is usable after startup failures.</summary>
public interface IPhase2Availability
{
    bool IsAvailable { get; }

    void MarkUnavailable();
}

/// <summary>Default availability state.</summary>
public sealed class Phase2Availability : IPhase2Availability
{
    public bool IsAvailable { get; private set; } = true;

    public void MarkUnavailable() => IsAvailable = false;
}
