using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Memory;

namespace FgoPet.App.Memory;

/// <summary>Application boundary for candidate review and servant-scoped memories.</summary>
public sealed class MemoryCandidateService
{
    private readonly SqliteMemoryRepository _repository;
    private readonly TimeProvider _clock;

    public MemoryCandidateService(SqliteMemoryRepository repository, TimeProvider clock)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<IReadOnlyList<MemoryCandidate>> ListCandidatesAsync(string servantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.ListCandidates(servantId));
    }

    public Task<StoredMemory?> ReviewAsync(
        string servantId,
        string candidateId,
        MemoryReviewAction action,
        string? editedText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.ReviewCandidate(
            candidateId,
            servantId,
            action,
            editedText,
            _clock.GetUtcNow()));
    }

    public Task<IReadOnlyList<StoredMemory>> ListEnabledAsync(string servantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.ListEnabledMemories(servantId));
    }

    public Task<IReadOnlyList<StoredMemory>> ListAllAsync(string servantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_repository.ListMemories(servantId));
    }

    public Task ReviewMemoryAsync(
        string servantId,
        string memoryId,
        MemoryReviewAction action,
        string? editedText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _repository.ReviewMemory(memoryId, servantId, action, editedText, _clock.GetUtcNow());
        return Task.CompletedTask;
    }
}
