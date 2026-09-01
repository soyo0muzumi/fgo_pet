using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.Core.Tests.Agents;

public sealed class AgentArchiveContractsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
    private const string Sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Archive_identity_rejects_non_terminal_status()
    {
        Assert.Throws<ArgumentException>(() => Identity(finalStatus: AgentExecutionStatus.Attention));
    }

    [Fact]
    public void Archive_identity_rejects_negative_final_sequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Identity(finalSequence: -1));
    }

    [Fact]
    public void Archive_candidate_rejects_missing_ended_at()
    {
        Assert.Throws<ArgumentException>(() => new AgentArchiveCandidate(
            "execution-1", Identity(), DateTimeOffset.MinValue, Sha256));
    }

    [Fact]
    public void Archive_candidate_rejects_non_uppercase_sha256()
    {
        Assert.Throws<ArgumentException>(() => new AgentArchiveCandidate(
            "execution-1", Identity(), At, Sha256.ToLowerInvariant()));
    }

    [Fact]
    public void Archive_batch_rejects_empty_or_oversized_candidate_sets()
    {
        Assert.Throws<ArgumentException>(() => Batch(Array.Empty<AgentArchiveCandidate>()));

        var oversized = Enumerable.Range(1, 129)
            .Select(index => Candidate(index))
            .ToArray();

        Assert.Throws<ArgumentException>(() => Batch(oversized));
    }

    [Fact]
    public void Archive_batch_rejects_duplicate_identity_even_when_sequence_and_status_differ()
    {
        var first = new AgentArchiveCandidate(
            "execution-1", Identity(finalSequence: 1, finalStatus: AgentExecutionStatus.Completed), At, Sha256);
        var second = new AgentArchiveCandidate(
            "execution-2", Identity(finalSequence: 2, finalStatus: AgentExecutionStatus.Failed), At.AddMinutes(1), Sha256);

        Assert.Throws<ArgumentException>(() => Batch(new[] { first, second }));
    }

    [Fact]
    public void Archive_batch_copies_candidates_for_immutable_contract()
    {
        var source = new List<AgentArchiveCandidate> { Candidate(1) };
        var batch = Batch(source);

        source.Clear();

        Assert.Single(batch.Candidates);
    }

    private static AgentArchiveIdentity Identity(
        long finalSequence = 1,
        AgentExecutionStatus finalStatus = AgentExecutionStatus.Completed) =>
        new("codex", "source-1", "task-1", "dispatch-1", finalSequence, finalStatus);

    private static AgentArchiveCandidate Candidate(int index) =>
        new(
            $"execution-{index}",
            new AgentArchiveIdentity("codex", "source-1", $"task-{index}", $"dispatch-{index}", index, AgentExecutionStatus.Completed),
            At.AddMinutes(index),
            Sha256);

    private static AgentArchiveBatch Batch(IReadOnlyList<AgentArchiveCandidate> candidates) =>
        new("batch-1", At, AgentArchiveBatchState.Preparing, candidates, Sha256);
}
