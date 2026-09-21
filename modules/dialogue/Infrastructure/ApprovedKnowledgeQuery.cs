using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;

namespace FgoPet.App.Dialogue;

/// <summary>
/// Selects approved Knowledge for one turn. Profile facts are ordinary context;
/// story facts require an explicit user request so casual dialogue stays small
/// and does not imply unsupported story claims.
/// </summary>
public sealed class ApprovedKnowledgeQuery
{
    private static readonly string[] StorySignals =
    [
        "剧情", "故事", "经历", "设定", "传记", "过去", "哪一章", "关系",
        "lore", "story", "plot", "chapter", "background",
    ];

    public IReadOnlyList<KnowledgeEntry> Select(
        ContentContextKey context,
        IReadOnlyList<KnowledgeEntry> entries,
        string userMessage)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var wantsStory = StorySignals.Any(signal =>
            userMessage.Contains(signal, StringComparison.OrdinalIgnoreCase));
        return entries
            .Where(entry => entry.IsApproved
                && entry.ServantId == context.ServantId
                && (entry.AppearanceId is null || entry.AppearanceId == context.AppearanceId)
                && (entry.Kind == KnowledgeKind.Profile || wantsStory))
            .OrderBy(entry => entry.Rank ?? int.MaxValue)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
