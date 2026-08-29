using FgoPet.Core.Memory;
using Xunit;

namespace FgoPet.Core.Tests.Memory;

public sealed class MemoryContractTests
{
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
