using System.Text;
using System.Text.RegularExpressions;
using FgoPet.Core.Memory;

namespace FgoPet.App.Memory;

public sealed class MemoryRecallService(IMemorySnapshotReader reader) : IMemoryRecall
{
    public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) =>
        Recall(scope, query, maxItems, maxChars, allowGeneralBackground: true);

    /// <summary>
    /// Matched memories only. Called by prompt injection, native context and the model-facing
    /// search tool, which must stay empty instead of receiving unrelated background.
    /// </summary>
    public MemoryRecallSnapshot QueryStrict(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) =>
        Recall(scope, query, maxItems, maxChars, allowGeneralBackground: false);

    private MemoryRecallSnapshot Recall(MemoryScope scope, string query, int maxItems, int maxChars,
        bool allowGeneralBackground)
    {
        var index = reader as IMemorySearchReader;
        var terms = Regex.Matches(query, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .SelectMany(match => Terms(match.Value, index is not null))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        var (snapshot, ranks) = index is null
            ? (reader.ReadSnapshot(scope), null)
            : index.ReadSearchSnapshot(scope, terms.Where(IsIndexTerm).ToArray());
        var literalTerms = index is null ? terms : terms.Where(term => !IsIndexTerm(term)).ToArray();
        var ranked = snapshot.Items.Where(m => m.IsEnabled && m.ServantId == scope.ServantId &&
                (m.ProjectId is null || m.ProjectId == scope.ProjectId))
            .Select(m => (Memory: m, IdMatch: query.Contains(m.MemoryId, StringComparison.Ordinal),
                Rank: ranks is not null && ranks.TryGetValue(m.MemoryId, out var rank) ? (double?)rank : null,
                // Trigram indexing stops at NUL in legacy text. Check the original in that case.
                LiteralMatches: (m.Text.Contains('\0') ? terms : literalTerms)
                    .Count(term => m.Text.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(pair => pair.IdMatch).ThenBy(pair => pair.Rank ?? double.PositiveInfinity)
            .ThenByDescending(pair => pair.LiteralMatches).ThenByDescending(pair => pair.Memory.UpdatedAtUtc)
            .ThenBy(pair => pair.Memory.MemoryId).ToArray();
        var matches = ranked.Where(pair => pair.IdMatch || pair.Rank.HasValue || pair.LiteralMatches > 0)
            .Select(pair => pair.Memory).ToArray();
        // Matched memories always win. The general-background fallback exists only for the
        // extraction prompt's `confirmed` context, where extra confirmed entries help the model
        // decide whether the user is correcting an existing memory. Every caller that injects
        // memories into a conversation uses QueryStrict and stays empty when nothing matches.
        var selected = matches.Length > 0 || !allowGeneralBackground
            ? matches
            : ranked.Where(pair => pair.Memory.ProjectId is null).Take(3).Select(pair => pair.Memory).ToArray();
        var result = new List<StoredMemory>();
        var remaining = Math.Clamp(maxChars, 0, 6000);
        foreach (var item in selected)
        {
            if (result.Count >= Math.Clamp(maxItems, 0, 8)) break;
            if (item.Text.Length > remaining) continue;
            result.Add(item); remaining -= item.Text.Length;
        }
        return new(snapshot.Revision, result);
    }
    private static bool IsIndexTerm(string term) => term.EnumerateRunes().Skip(2).Any();

    private static IEnumerable<string> Terms(string text, bool includeTrigrams)
    {
        yield return text;
        // Chinese phrases need searchable slices even when the whole sentence is one token.
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (!IsHan(text[i]) || !IsHan(text[i + 1])) continue;
            if (includeTrigrams && i + 2 < text.Length && IsHan(text[i + 2]))
                yield return text.Substring(i, 3);
            yield return text.Substring(i, 2);
        }
    }

    private static bool IsHan(char value) => value is >= '\u3400' and <= '\u9fff';
}
