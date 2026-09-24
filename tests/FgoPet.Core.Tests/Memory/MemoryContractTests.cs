using FgoPet.Core.Memory;
using Xunit;

namespace FgoPet.Core.Tests.Memory;

public sealed class MemoryContractTests
{
    [Fact]
    public void Provenance_requires_valid_scope_evidence_and_exact_replacement_pair()
    {
        Assert.Throws<ArgumentException>(() => new MemoryScope("", null));
        Assert.Throws<ArgumentException>(() => new MemoryProposal(new string('x', 2001)));
        Assert.Throws<ArgumentException>(() => new MemoryProposal("text", "id"));
        Assert.Throws<ArgumentException>(() => new MemoryProposal("text", expectedMemoryVersion: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemorySource(new("a", null), "c", "m", "f", DateTimeOffset.UtcNow, (MemoryEvidenceKind)99));
        var source = new MemorySource(new("a", "p"), "c", "m", "f", DateTimeOffset.UtcNow, MemoryEvidenceKind.UserStatement);
        Assert.Throws<ArgumentException>(() => new MemoryCandidate("id", "a", "c", "text", DateTimeOffset.UtcNow, "m", projectId: "other", source: source));
        Assert.Throws<ArgumentException>(() => new StoredMemory("id", "b", "text", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, source: source));
    }
    [Fact]
    public void New_memory_candidate_is_pending_and_servant_scoped()
    {
        var candidate = new MemoryCandidate(
            "candidate-1",
            "800100",
            "conversation-1",
            "用户喜欢安静地工作",
            DateTimeOffset.UtcNow);

        Assert.Equal(MemoryCandidateStatus.Pending, candidate.Status);
        Assert.Equal("800100", candidate.ServantId);
        Assert.Null(candidate.AppearanceId);
    }

    [Fact]
    public void Stored_memory_requires_explicit_enabled_state()
    {
        var now = DateTimeOffset.UtcNow;
        var memory = new StoredMemory("memory-1", "800100", "用户喜欢安静地工作", false, now, now);

        Assert.False(memory.IsEnabled);
        Assert.Equal("800100", memory.ServantId);
    }

    [Fact]
    public void Memory_candidate_rejects_empty_text()
    {
        Assert.Throws<ArgumentException>(() =>
            new MemoryCandidate("candidate-1", "800100", "conversation-1", "", DateTimeOffset.UtcNow));
    }
}
