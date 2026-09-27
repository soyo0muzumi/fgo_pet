using System.Text.RegularExpressions;
using FgoPet.Core.Memory;

namespace FgoPet.App.Memory;

public sealed class MemoryRecallService(IMemorySnapshotReader reader) : IMemoryRecall
{
    public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000)
    {
        var snapshot = reader.ReadSnapshot(scope);
        var terms = Regex.Matches(query, @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .SelectMany(match => Terms(match.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
        var ranked = snapshot.Items.Where(m => m.IsEnabled && m.ServantId == scope.ServantId &&
                (m.ProjectId is null || m.ProjectId == scope.ProjectId))
            .Select(m => (Memory: m, Score: query.Contains(m.MemoryId, StringComparison.Ordinal) ? 10000 :
                terms.Count(t => m.Text.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(pair => pair.Score).ThenByDescending(pair => pair.Memory.UpdatedAtUtc).ThenBy(pair => pair.Memory.MemoryId).ToArray();
        var matches = ranked.Where(pair => pair.Score > 0).Select(pair => pair.Memory).ToArray();
        var selected = matches.Length > 0 ? matches : ranked.Where(pair => pair.Memory.ProjectId is null).Take(3).Select(pair => pair.Memory).ToArray();
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
    private static IEnumerable<string> Terms(string text)
    {
        yield return text;
        // Chinese has no spaces: contiguous Han bigrams provide bounded literal matching.
        for (var i = 0; i + 1 < text.Length; i++)
            if (text[i] is >= '\u3400' and <= '\u9fff' && text[i + 1] is >= '\u3400' and <= '\u9fff') yield return text.Substring(i, 2);
    }
}
