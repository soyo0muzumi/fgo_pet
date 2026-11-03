using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Understanding.Contracts;

/// <summary>A retrieval hint about observed behaviour, never a Self, fact, persona or promotion input.</summary>
public sealed record RecurrenceClue(string Id, string Theme, string Summary,
    IReadOnlyList<ExperienceSourceHead> SourceRefs, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt)
{
    public bool Projectable => false;
}

/// <summary>Quotes only attest that an observation was made; they cannot establish autonomous authorship.</summary>
public sealed record RecurrenceClueDraft(string Theme, string Summary, IReadOnlyList<UnderstandingExpression> Expressions);

public sealed record RecurrenceDiscovery(IReadOnlyList<RecurrenceClueDraft> Clues, IReadOnlyList<string> RetrievedIds);
public sealed record RecurrenceSelection(IReadOnlyList<string> BroadIds, IReadOnlyList<string> RetrievedIds);

public interface IRecurrenceModel
{
    Task<RecurrenceDiscovery> DiscoverAsync(ReviewModelInput current, IReadOnlyList<RecurrenceClue> available,
        CancellationToken token = default);
    Task<RecurrenceSelection> RetrieveAsync(ReviewModelInput current, IReadOnlyList<RecurrenceClue> available,
        RecurrenceDiscovery discovery, CancellationToken token = default)
        => Task.FromResult(new RecurrenceSelection(discovery.RetrievedIds, discovery.RetrievedIds));
}

/// <summary>Trusted source host attests the original generation omitted prior Understanding/clues/maintainer summaries.
/// It must also exclude cross-session continuation and user reminders. A model cannot grant this attestation.</summary>
public interface IRecurrenceIsolationReader
{
    bool IsIsolated(ConversationSourceSnapshot source);
}
