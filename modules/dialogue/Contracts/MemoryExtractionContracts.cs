using FgoPet.Core.Memory;
using FgoPet.Core.Settings;

namespace FgoPet.Core.Dialogue;

public sealed record MemoryExtractionWork(MemoryWriteTicket Ticket, string UserText, string AssistantText,
    ModelConnectionSettings Connection, CancellationToken CancellationToken);
public interface IMemoryCandidateExtractor
{
    Task<IReadOnlyList<MemoryProposal>> ExtractAsync(MemoryExtractionWork work, CancellationToken cancellationToken);
}
