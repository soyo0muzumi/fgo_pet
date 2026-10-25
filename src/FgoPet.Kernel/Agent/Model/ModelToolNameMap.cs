using System.Security.Cryptography;
using System.Text;

namespace FgoPet.Kernel.Agent;

/// <summary>Exact request-local resolution. Historical wire names do not depend on current availability.</summary>
public sealed class ModelToolNameMap
{
    private readonly Dictionary<string, string> _canonicalByAlias = new(StringComparer.Ordinal);

    public ModelToolNameMap(IEnumerable<string> canonicalNames)
    {
        ArgumentNullException.ThrowIfNull(canonicalNames);
        foreach (var canonical in canonicalNames)
            if (!_canonicalByAlias.TryAdd(GetWireName(canonical), canonical))
                throw new AgentProtocolException("MODEL_TOOL_ALIAS_COLLISION");
    }

    public bool TryResolve(string alias, out string canonical) => _canonicalByAlias.TryGetValue(alias, out canonical!);
    public string ToWireName(string canonical) => GetWireName(canonical);

    public static string GetWireName(string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical) || canonical.Length > 64 || canonical.Any(char.IsWhiteSpace)
            || canonical.Any(char.IsControl)) throw new AgentProtocolException("MODEL_INVALID_TOOL_NAME");
        if (canonical.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            return canonical;
        return "fgo_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..60].ToLowerInvariant();
    }
}
