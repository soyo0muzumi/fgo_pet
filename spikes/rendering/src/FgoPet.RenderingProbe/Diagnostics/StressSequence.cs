using System.IO;

namespace FgoPet.RenderingProbe.Diagnostics;

public static class StressSequence
{
    public static IReadOnlyList<string> Create(IEnumerable<string> stableIds)
    {
        var expressions = stableIds
            .Where(id => id != "full_body")
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (expressions.Length != 28)
        {
            throw new InvalidDataException($"Stress run requires 28 expressions; found {expressions.Length}.");
        }
        return Enumerable.Range(0, 10).SelectMany(_ => expressions).ToArray();
    }
}
