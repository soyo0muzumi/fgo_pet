using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

internal static class UnderstandingReviewResponse
{
    internal static IReadOnlyList<UnderstandingProposal> Parse(string json, string binding,
        IReadOnlyList<ReviewOriginal> originals, IReadOnlyList<UnderstandingEntry> targets, bool hostBoundExpressions = false)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new AgentProtocolException("UNDERSTANDING_REVIEW_INVALID_JSON"); }
        using (document)
        {
            var root = document.RootElement;
            Exact(root, "schema", "request", "proposals");
            if (root.GetProperty("schema").ValueKind != JsonValueKind.Number || !root.GetProperty("schema").TryGetInt32(out var schema) ||
                schema != (hostBoundExpressions ? 4 : 3) || Text(root, "request", 64) != binding) throw Invalid();
            var items = root.GetProperty("proposals");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 32) throw Invalid();
            var result = new List<UnderstandingProposal>();
            var revised = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                Exact(item, "domain", "kind", "subject", "content", "conditions", "sources", "target",
                    "operation", "origin", "status", "expressions", "conflicts", "maintenance_intents");
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
                if (!UnderstandingWritePolicy.IsAllowedKind(domain, kind)) throw Invalid();
                var operation = Named<UnderstandingWriteOperation>(Text(item, "operation", 32));
                var origin = Named<UnderstandingOrigin>(Text(item, "origin", 32));
                var status = Named<UnderstandingStatus>(Text(item, "status", 32));
                if ((operation == UnderstandingWriteOperation.Create) != (target is null) ||
                    (operation == UnderstandingWriteOperation.Retract ? status != UnderstandingStatus.Retracted :
                        status is not (UnderstandingStatus.Hypothesis or UnderstandingStatus.Tentative))) throw Invalid();
                var aliases = item.GetProperty("sources");
                if (aliases.ValueKind != JsonValueKind.Array || aliases.GetArrayLength() < 1 || aliases.GetArrayLength() > originals.Count) throw Invalid();
                var selected = new List<ConversationSourceSnapshot>();
                foreach (var alias in aliases.EnumerateArray())
                {
                    var source = Source(alias, originals);
                    if (selected.Contains(source)) throw Invalid();
                    selected.Add(source);
                }
                var expressionItems = item.GetProperty("expressions");
                if (expressionItems.ValueKind != JsonValueKind.Array || expressionItems.GetArrayLength() > 8) throw Invalid();
                var expressions = new List<UnderstandingExpression>();
                foreach (var expression in expressionItems.EnumerateArray())
                {
                    if (hostBoundExpressions)
                    {
                        var boundSource = Source(expression, originals);
                        var raw = originals.Single(o => o.Source == boundSource);
                        if (!selected.Contains(boundSource) || boundSource.Kind != ConversationSourceKind.Assistant ||
                            !raw.Coverage.Complete || raw.Text.Length > 2000 || expressions.Any(e => e.Source == boundSource)) throw Invalid();
                        expressions.Add(new(boundSource, raw.Text));
                        continue;
                    }
                    Exact(expression, "source", "quote");
                    var source = Source(expression.GetProperty("source"), originals);
                    var quote = Text(expression, "quote", 2000);
                    if (!selected.Contains(source) || expressions.Any(e => e.Source == source && e.Quote == quote)) throw Invalid();
                    expressions.Add(new(source, quote));
                }
                var conflictItems = item.GetProperty("conflicts");
                if (conflictItems.ValueKind != JsonValueKind.Array || conflictItems.GetArrayLength() > originals.Count) throw Invalid();
                var conflicts = new List<ConversationSourceSnapshot>();
                foreach (var conflict in conflictItems.EnumerateArray())
                {
                    var source = Source(conflict, originals);
                    if (!selected.Contains(source) || conflicts.Contains(source)) throw Invalid();
                    conflicts.Add(source);
                }
                var intentItems = item.GetProperty("maintenance_intents");
                if (intentItems.ValueKind != JsonValueKind.Array || intentItems.GetArrayLength() > 4) throw Invalid();
                var intents = new List<UnderstandingMaintenanceIntent>();
                foreach (var intent in intentItems.EnumerateArray())
                {
                    if (intent.ValueKind != JsonValueKind.String || intent.GetString() is not { } name) throw Invalid();
                    var value = Named<UnderstandingMaintenanceIntent>(name);
                    if (intents.Contains(value)) throw Invalid();
                    intents.Add(value);
                }
                result.Add(new("review_" + binding + "_" + (result.Count + 1), domain, kind,
                    Text(item, "subject", 128), Text(item, "content", 2000), Text(item, "conditions", 1000),
                    Array.AsReadOnly(selected.ToArray()), target?.Id, target?.Revision, origin, status, operation,
                    Array.AsReadOnly(expressions.ToArray()), Array.AsReadOnly(conflicts.ToArray()), Array.AsReadOnly(intents.ToArray())));
            }
            return Array.AsReadOnly(result.ToArray());
        }
    }
    private static ConversationSourceSnapshot Source(JsonElement alias, IReadOnlyList<ReviewOriginal> originals)
    {
        if (alias.ValueKind != JsonValueKind.String) throw Invalid();
        var index = Enumerable.Range(0, originals.Count).FirstOrDefault(i => "s" + (i + 1) == alias.GetString(), -1);
        return index >= 0 ? originals[index].Source : throw Invalid();
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
