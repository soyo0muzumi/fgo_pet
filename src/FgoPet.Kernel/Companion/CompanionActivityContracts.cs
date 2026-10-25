using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Companion;

public sealed record CompanionActivityLimits
{
    public int MaxInputTokens { get; init; } = 4096;
    public int MaxOutputTokens { get; init; } = 384;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ResultLifetime { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Null host capture means that the activity is not enabled or its actual scope/model is unavailable.</summary>
public sealed record CompanionActivityConfiguration(CompanionContextSnapshot Context,
    ModelConnectionSettings Connection, string PolicyRevision, CompanionActivityLimits Limits);

public interface ICompanionActivityHost
{
    /// <summary>Capture owner facts only; previously generated interpretations must not become new evidence.</summary>
    CompanionActivityConfiguration? Capture();

    /// <summary>
    /// Execute the synchronous callback only while the captured source scope, permissions, model and policy
    /// are still current. Changes and this callback share the host's authority lock. Never retain the callback.
    /// </summary>
    bool TryCommit(CompanionActivityConfiguration configuration, Action action);
}

/// <summary>Independent activity identity; never a fabricated conversation message or native user-run identity.</summary>
public sealed record CompanionActivityRequest(string ActivityId, string Reason, DateTimeOffset CreatedAtUtc,
    CompanionActivityConfiguration Configuration);

public sealed record CompanionActivityReservation(string ActivityId, ToolScope Scope, ModelRouteKey Route,
    string PolicyRevision, DateTimeOffset ReservedAtUtc, int InputTokens, int MaxOutputTokens);

public interface ICompanionActivityBudget
{
    /// <summary>
    /// Atomically persist a charge at the final HTTP handoff. Reject unavailable/corrupt storage and repeated
    /// activity IDs. A dispatched, failed, cancelled or uncertain attempt is never refunded.
    /// </summary>
    bool TryReserve(CompanionActivityReservation reservation);
}

/// <summary>The adapter attests which prefix of the original observation was actually supplied to the model.</summary>
public sealed record CompanionActivitySourceCoverage(string SourceId, int ReadCharacters, int TotalCharacters);

/// <summary>Null Interpretation means no change. Missing coverage is unknown, never evidence of a complete read.</summary>
public sealed record CompanionActivityJudgment(string? Interpretation, IReadOnlyList<string> Sources,
    IReadOnlyList<CompanionActivitySourceCoverage>? SourceCoverage = null);

public interface ICompanionActivityModel
{
    Task<CompanionActivityJudgment> EvaluateAsync(CompanionActivityRequest request, IChatDispatchGate dispatchGate,
        Action<CancellationToken> ensureCurrent, CancellationToken cancellationToken);
}

public sealed record CompanionActivityDiagnostic(DateTimeOffset Timestamp, string ActivityId,
    string Stage, string? ErrorCode = null);
