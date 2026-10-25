using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

internal static class UnderstandingReviewResponse
{
    internal static IReadOnlyList<UnderstandingProposal> Parse(string json, string binding,
        IReadOnlyList<ReviewOriginal> originals, IReadOnlyList<UnderstandingEntry> targets)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new AgentProtocolException("UNDERSTANDING_REVIEW_INVALID_JSON"); }
        using (document)
        {
            var root = document.RootElement;
            Exact(root, "schema", "request", "proposals");
            if (root.GetProperty("schema").ValueKind != JsonValueKind.Number || !root.GetProperty("schema").TryGetInt32(out var schema) ||
                schema != 2 || Text(root, "request", 64) != binding) throw Invalid();
            var items = root.GetProperty("proposals");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 32) throw Invalid();
            var result = new List<UnderstandingProposal>();
            var revised = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                Exact(item, "domain", "kind", "subject", "content", "conditions", "sources", "target");
                UnderstandingEntry? target = null;
                if (item.GetProperty("target").ValueKind != JsonValueKind.Null)
                {
                    var alias = Text(item, "target", 16);
                    var index = Enumerable.Range(0, targets.Count).FirstOrDefault(i => "t" + (i + 1) == alias, -1);
                    if (index < 0 || !revised.Add(targets[index].Id)) throw Invalid();
                    target = targets[index];
                }
                var domain = Named<UnderstandingDomain>(Text(item, "domain", 32));
                var kind = Named<UnderstandingKind>(Text(item, "kind", 32));
                var aliases = item.GetProperty("sources");
                if (aliases.ValueKind != JsonValueKind.Array || aliases.GetArrayLength() < 1 || aliases.GetArrayLength() > originals.Count) throw Invalid();
                var selected = new List<ConversationSourceSnapshot>();
                foreach (var alias in aliases.EnumerateArray())
                {
                    if (alias.ValueKind != JsonValueKind.String) throw Invalid();
                    var index = Enumerable.Range(0, originals.Count).FirstOrDefault(i => "s" + (i + 1) == alias.GetString(), -1);
                    if (index < 0 || selected.Contains(originals[index].Source)) throw Invalid();
                    selected.Add(originals[index].Source);
                }
                result.Add(new("review_" + binding + "_" + (result.Count + 1), domain, kind,
                    Text(item, "subject", 128), Text(item, "content", 2000), Text(item, "conditions", 1000),
                    Array.AsReadOnly(selected.ToArray()), target?.Id, target?.Revision));
            }
            return Array.AsReadOnly(result.ToArray());
        }
    }
    private static void Exact(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != keys.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            !names.ToHashSet(StringComparer.Ordinal).SetEquals(keys)) throw Invalid();
    }
    private static string Text(JsonElement parent, string name, int limit)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > limit) throw Invalid();
        return text;
    }
    private static T Named<T>(string name) where T : struct, Enum =>
        Enum.TryParse<T>(name, out var value) && Enum.IsDefined(value) && value.ToString() == name ? value : throw Invalid();
    private static AgentProtocolException Invalid() => new("UNDERSTANDING_REVIEW_INVALID_RESPONSE");
}
