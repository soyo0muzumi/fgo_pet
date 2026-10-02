namespace FgoPet.App.Dialogue;

public sealed record DialogueProjectOption(
    string Id,
    string Label,
    string Detail,
    bool IsReadOnly,
    string? CurrentBranch = null,
    string? Revision = null,
    string? ContextVersion = null)
{
    /// <summary>Safe branch descriptors supplied by the local target catalog.</summary>
    public IReadOnlyList<string> Branches { get; init; } = Array.Empty<string>();
    public string Access { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset? RefreshedAtUtc { get; init; }
}

public sealed record DialogueProjectCatalogResult(
    bool IsAvailable,
    IReadOnlyList<DialogueProjectOption> Projects,
    string? SafeError = null);

/// <summary>Dialogue-facing project discovery. Implementations expose no local paths.</summary>
public interface IDialogueProjectCatalog
{
    Task<DialogueProjectCatalogResult> ListAsync(CancellationToken cancellationToken = default);
}
