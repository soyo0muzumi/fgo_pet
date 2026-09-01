namespace FgoPet.Core.Agents;

public enum AgentArchiveBatchState
{
    Preparing,
    Prepared,
    CommitPending,
    Completed,
    Rejected,
}

public sealed record AgentArchiveIdentity(
    string SourceType,
    string SourceInstance,
    string TaskId,
    string DispatchRequestId,
    long FinalSequence,
    AgentExecutionStatus FinalStatus)
{
    public string SourceType { get; } = AgentIdentityValidation.Id(SourceType, nameof(SourceType));
    public string SourceInstance { get; } = AgentIdentityValidation.Id(SourceInstance, nameof(SourceInstance));
    public string TaskId { get; } = AgentIdentityValidation.Id(TaskId, nameof(TaskId));
    public string DispatchRequestId { get; } = AgentIdentityValidation.Id(DispatchRequestId, nameof(DispatchRequestId));
    public long FinalSequence { get; } = ValidateSequence(FinalSequence);
    public AgentExecutionStatus FinalStatus { get; } = ValidateStatus(FinalStatus);

    private static long ValidateSequence(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FinalSequence), "Final sequence cannot be negative.");
        }

        return value;
    }

    private static AgentExecutionStatus ValidateStatus(AgentExecutionStatus value)
    {
        if (value is not (AgentExecutionStatus.Completed or AgentExecutionStatus.Failed or AgentExecutionStatus.Cancelled))
        {
            throw new ArgumentException("Archive identities require a terminal execution status.", nameof(FinalStatus));
        }

        return value;
    }
}

public sealed record AgentArchiveCandidate(
    string ExecutionId,
    AgentArchiveIdentity Identity,
    DateTimeOffset EndedAt,
    string SummarySha256)
{
    public string ExecutionId { get; } = AgentIdentityValidation.Id(ExecutionId, nameof(ExecutionId));
    public AgentArchiveIdentity Identity { get; } = Identity ?? throw new ArgumentNullException(nameof(Identity));
    public DateTimeOffset EndedAt { get; } = ValidateEndedAt(EndedAt);
    public string SummarySha256 { get; } = AgentArchiveValidation.Sha256(SummarySha256, nameof(SummarySha256));

    private static DateTimeOffset ValidateEndedAt(DateTimeOffset value)
    {
        if (value == default || value == DateTimeOffset.MinValue)
        {
            throw new ArgumentException("Archive candidates require endedAt.", nameof(EndedAt));
        }

        return value;
    }
}

public sealed record AgentArchiveBatch(
    string BatchId,
    DateTimeOffset CreatedAt,
    AgentArchiveBatchState State,
    IReadOnlyList<AgentArchiveCandidate> Candidates,
    string BatchSha256,
    string? SafeError = null)
{
    public string BatchId { get; } = AgentIdentityValidation.Id(BatchId, nameof(BatchId));
    public DateTimeOffset CreatedAt { get; } = ValidateCreatedAt(CreatedAt);
    public AgentArchiveBatchState State { get; } = ValidateState(State);
    public IReadOnlyList<AgentArchiveCandidate> Candidates { get; } = CopyCandidates(Candidates);
    public string BatchSha256 { get; } = AgentArchiveValidation.Sha256(BatchSha256, nameof(BatchSha256));
    public string? SafeError { get; } = SafeError;

    private static DateTimeOffset ValidateCreatedAt(DateTimeOffset value)
    {
        if (value == default || value == DateTimeOffset.MinValue)
        {
            throw new ArgumentException("Archive batches require createdAt.", nameof(CreatedAt));
        }

        return value;
    }

    private static AgentArchiveBatchState ValidateState(AgentArchiveBatchState value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException("Unknown archive batch state.", nameof(State));
        }

        return value;
    }

    private static IReadOnlyList<AgentArchiveCandidate> CopyCandidates(IReadOnlyList<AgentArchiveCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count is 0 or > 128)
        {
            throw new ArgumentException("Archive batches must contain between 1 and 128 candidates.", nameof(candidates));
        }

        var copy = candidates.ToArray();
        if (copy.Any(candidate => candidate is null))
        {
            throw new ArgumentException("Archive batches cannot contain null candidates.", nameof(candidates));
        }

        if (copy
            .Select(candidate => (
                candidate.Identity.SourceType,
                candidate.Identity.SourceInstance,
                candidate.Identity.TaskId,
                candidate.Identity.DispatchRequestId))
            .Distinct()
            .Count() != copy.Length)
        {
            throw new ArgumentException("Archive batches cannot contain duplicate identities.", nameof(candidates));
        }

        return Array.AsReadOnly(copy);
    }
}

internal static class AgentArchiveValidation
{
    public static string Sha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new ArgumentException($"{parameterName} must be an uppercase 64-character SHA-256 hash.", parameterName);
        }

        return value;
    }
}
