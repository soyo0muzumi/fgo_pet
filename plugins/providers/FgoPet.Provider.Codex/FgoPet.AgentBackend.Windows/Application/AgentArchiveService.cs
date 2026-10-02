using FgoPet.Core.Agents;
using FgoPet.Infrastructure.Agents;

namespace FgoPet.App.Services;

public sealed record AgentArchiveRunResult(
    string Result,
    string? BatchId = null,
    int CandidateCount = 0,
    string? SafeError = null);

/// <summary>
/// Coordinates the App-side half of the relay/adapter archive protocol. Every
/// network mutation is preceded by a durable SQLite state transition, and an
/// unknown network outcome remains resumable instead of creating a replacement
/// batch or retrying blindly.
/// </summary>
public sealed class AgentArchiveService
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    public const int MaxCandidates = 128;

    private readonly IAgentRepository _agents;
    private readonly IAgentRelayAdministration _administration;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;

    public AgentArchiveService(
        IAgentRepository agents,
        IAgentRelayAdministration administration,
        TimeProvider time,
        TimeSpan? retention = null)
    {
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _retention = retention ?? DefaultRetention;
        if (_retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public IReadOnlyList<AgentArchiveCandidate> BuildCandidates()
    {
        var cutoff = _time.GetUtcNow() - _retention;
        var candidates = new List<AgentArchiveCandidate>();
        foreach (var execution in _agents.ListTerminalExecutions(cutoff, MaxCandidates))
        {
            if (execution.EndedAt is null) continue;
            var finalSequence = _agents.GetLatestEventSequence(
                execution.SourceType, execution.SourceInstance, execution.TaskId);
            if (finalSequence < 1
                || !_agents.HasEventReceipt(
                    execution.SourceType, execution.SourceInstance, execution.TaskId, finalSequence))
                continue;

            var identity = new AgentArchiveIdentity(
                execution.SourceType,
                execution.SourceInstance,
                execution.TaskId,
                execution.DispatchRequestId,
                finalSequence,
                execution.Status);
            candidates.Add(new AgentArchiveCandidate(
                execution.Id,
                identity,
                execution.EndedAt.Value,
                AgentArchiveHashing.CandidateSha256(identity, execution.EndedAt.Value)));
        }

        return candidates
            .OrderBy(candidate => candidate.EndedAt)
            .ThenBy(candidate => candidate.ExecutionId, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToArray();
    }

    public async Task<AgentArchiveRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = _agents.ListIncompleteArchiveBatches()
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.BatchId, StringComparer.Ordinal)
            .FirstOrDefault();
        var activeExecutions = _agents.ListNonTerminalExecutions();
        if (activeExecutions.Count > 0)
        {
            return new AgentArchiveRunResult(
                "blocked_active_work",
                batch?.BatchId,
                batch?.Candidates.Count ?? 0,
                "active_agent_work");
        }

        if (batch is null)
        {
            var candidates = BuildCandidates();
            if (candidates.Count == 0)
                return new AgentArchiveRunResult("no_candidates");
            batch = new AgentArchiveBatch(
                "archive-" + Guid.NewGuid().ToString("N"),
                _time.GetUtcNow(),
                AgentArchiveBatchState.Preparing,
                candidates,
                AgentArchiveHashing.BatchSha256(candidates.Select(item => item.SummarySha256)));
            _agents.SaveArchiveBatch(batch);
        }

        if (batch.State == AgentArchiveBatchState.Preparing)
        {
            var prepare = await _administration.PrepareArchiveAsync(batch, cancellationToken).ConfigureAwait(false);
            if (IsUnknown(prepare.SafeError))
                return new AgentArchiveRunResult("unknown", batch.BatchId, batch.Candidates.Count, prepare.SafeError);
            if (prepare.Result == "rejected")
            {
                var rejected = Transition(batch, AgentArchiveBatchState.Rejected,
                    SafeError(prepare.SafeError ?? "archive_prepare_rejected"));
                _agents.SaveArchiveBatch(rejected);
                return new AgentArchiveRunResult("rejected", batch.BatchId, batch.Candidates.Count, rejected.SafeError);
            }
            if (prepare.Result is not ("accepted" or "already_prepared" or "prepared" or "ok"))
                return new AgentArchiveRunResult("unknown", batch.BatchId, batch.Candidates.Count, "operation_unknown");

            batch = Transition(batch, AgentArchiveBatchState.Prepared);
            _agents.SaveArchiveBatch(batch);
        }

        if (batch.State == AgentArchiveBatchState.Prepared)
        {
            batch = Transition(batch, AgentArchiveBatchState.CommitPending);
            _agents.SaveArchiveBatch(batch);
        }

        if (batch.State != AgentArchiveBatchState.CommitPending)
        {
            return new AgentArchiveRunResult(
                batch.State == AgentArchiveBatchState.Completed ? "completed" : "rejected",
                batch.BatchId,
                batch.Candidates.Count,
                batch.SafeError);
        }

        var commit = await _administration.CommitArchiveAsync(
            batch.BatchId, batch.BatchSha256, cancellationToken).ConfigureAwait(false);
        if (IsUnknown(commit.SafeError))
            return new AgentArchiveRunResult("unknown", batch.BatchId, batch.Candidates.Count, commit.SafeError);
        if (commit.Result == "rejected")
        {
            var rejected = Transition(batch, AgentArchiveBatchState.Rejected,
                SafeError(commit.SafeError ?? "archive_commit_rejected"));
            _agents.SaveArchiveBatch(rejected);
            return new AgentArchiveRunResult("rejected", batch.BatchId, batch.Candidates.Count, rejected.SafeError);
        }
        if (commit.Result is not ("accepted" or "already_committed" or "committed" or "ok"))
            return new AgentArchiveRunResult("unknown", batch.BatchId, batch.Candidates.Count, "operation_unknown");

        _agents.CompleteArchiveBatch(batch.BatchId, _time.GetUtcNow());
        return new AgentArchiveRunResult("completed", batch.BatchId, batch.Candidates.Count);
    }

    private static bool IsUnknown(string? safeError) =>
        safeError is "relay_timeout" or "relay_offline" or "invalid_response" or "operation_unknown";

    private static AgentArchiveBatch Transition(
        AgentArchiveBatch batch,
        AgentArchiveBatchState state,
        string? safeError = null) =>
        new(batch.BatchId, batch.CreatedAt, state, batch.Candidates, batch.BatchSha256, safeError);

    private static string SafeError(string value)
    {
        var compact = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return compact.Length <= 512 ? compact : compact[..512];
    }
}
