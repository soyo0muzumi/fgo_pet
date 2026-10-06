using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Memory;

/// <summary>Read-only native tools over the memory owner's current recall contract.</summary>
public static class NativeMemoryTools
{
    private const int MaxQueryScalars = 2_000;
    private const int MaxItems = 8;
    private const int MaxChars = 6_000;
    private const int MaxArgumentsBytes = 32 * 1024;
    private const int MaxRecallItemsToInspect = 64;
    private const int MaxOutputBytes = 48 * 1024;

    private static readonly JsonSerializerOptions OutputJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<IToolProvider> Create(IMemoryRecall recall, Func<bool>? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(recall);
        return Array.AsReadOnly<IToolProvider>([new SearchProvider(recall, enabled)]);
    }

    private sealed class SearchProvider(IMemoryRecall recall, Func<bool>? enabled) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = new(
            "memory.search",
            "Search enabled confirmed memories for the current role and project.",
            """
            {"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":2000},
            "maxItems":{"type":"integer","minimum":1,"maximum":8},
            "maxChars":{"type":"integer","minimum":1,"maximum":6000}},
            "required":["query"],"additionalProperties":false}
            """, ToolEffect.ReadOnly);

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsEnabled())
                    return ValueTask.FromResult(Failure("MEMORY_UNAVAILABLE"));
                if (invocation is null || !TryParseArguments(invocation.Arguments, out var arguments))
                    return ValueTask.FromResult(Failure("TOOL_INVALID_ARGUMENTS"));
                if (invocation.Scope is null || string.IsNullOrWhiteSpace(invocation.Scope.RoleId)
                    || invocation.Scope.ProjectId is not null && string.IsNullOrWhiteSpace(invocation.Scope.ProjectId))
                    return ValueTask.FromResult(Failure("TOOL_SCOPE_INVALID"));

                MemoryScope scope;
                try { scope = new MemoryScope(invocation.Scope.RoleId, invocation.Scope.ProjectId); }
                catch (ArgumentException) { return ValueTask.FromResult(Failure("TOOL_SCOPE_INVALID")); }

                if (!IsEnabled())
                    return ValueTask.FromResult(Failure("MEMORY_UNAVAILABLE"));
                var snapshot = recall.Query(scope, arguments.Query, arguments.MaxItems, arguments.MaxChars);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsEnabled())
                    return ValueTask.FromResult(Failure("MEMORY_UNAVAILABLE"));
                if (snapshot?.Items is null || snapshot.Revision < 0)
                    return ValueTask.FromResult(Failure("MEMORY_SEARCH_FAILED"));

                var results = new List<SearchItem>();
                var charsUsed = 0;
                var truncated = snapshot.Items.Count > MaxRecallItemsToInspect;
                var itemsToInspect = Math.Min(snapshot.Items.Count, MaxRecallItemsToInspect);

                for (var index = 0; index < itemsToInspect; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var memory = snapshot.Items[index];
                    if (memory is null || !memory.IsEnabled || memory.ServantId != scope.ServantId
                        || memory.ProjectId is not null && memory.ProjectId != scope.ProjectId)
                        continue;

                    if (results.Count >= arguments.MaxItems)
                    {
                        truncated = true;
                        break;
                    }

                    var text = memory.Text;
                    if (text is null)
                    {
                        truncated = true;
                        continue;
                    }
                    var textScalars = CountScalars(text);
                    if (textScalars > arguments.MaxChars - charsUsed)
                    {
                        truncated = true;
                        continue;
                    }

                    var candidate = new SearchItem(memory.MemoryId, memory.Version, text, memory.ProjectId);
                    var preview = new SearchPayload(snapshot.Revision, [.. results, candidate], Truncated: true);
                    if (!Fits(preview))
                    {
                        truncated = true;
                        break;
                    }

                    results.Add(candidate);
                    charsUsed += textScalars;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsEnabled())
                    return ValueTask.FromResult(Failure("MEMORY_UNAVAILABLE"));
                var payload = new SearchPayload(snapshot.Revision, results, truncated);
                var fits = Fits(payload);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsEnabled())
                    return ValueTask.FromResult(Failure("MEMORY_UNAVAILABLE"));
                if (!fits) return ValueTask.FromResult(Failure("TOOL_OUTPUT_LIMIT"));
                return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(payload, OutputJson)));
            }
            catch (OperationCanceledException)
            {
                return ValueTask.FromResult(Failure("TOOL_CANCELLED"));
            }
            catch (Exception)
            {
                // Recall may surface private data in exception messages; return only a stable code.
                return ValueTask.FromResult(Failure(IsEnabled() ? "MEMORY_SEARCH_FAILED" : "MEMORY_UNAVAILABLE"));
            }
        }

        private bool IsEnabled()
        {
            try { return enabled?.Invoke() ?? true; }
            catch (Exception) { return false; }
        }

        private static bool TryParseArguments(JsonElement value, out SearchArguments result)
        {
            result = new SearchArguments(string.Empty, MaxItems, MaxChars);
            if (value.ValueKind != JsonValueKind.Object
                || Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxArgumentsBytes)
                return false;

            var querySeen = false;
            var maxItemsSeen = false;
            var maxCharsSeen = false;
            var query = string.Empty;
            var maxItems = MaxItems;
            var maxChars = MaxChars;

            foreach (var property in value.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "query":
                        if (querySeen || property.Value.ValueKind != JsonValueKind.String) return false;
                        querySeen = true;
                        query = property.Value.GetString() ?? string.Empty;
                        var queryLength = CountScalars(query);
                        if (queryLength is < 1 or > MaxQueryScalars || string.IsNullOrWhiteSpace(query)) return false;
                        break;
                    case "maxItems":
                        if (maxItemsSeen || !TryReadInteger(property.Value, 1, MaxItems, out maxItems)) return false;
                        maxItemsSeen = true;
                        break;
                    case "maxChars":
                        if (maxCharsSeen || !TryReadInteger(property.Value, 1, MaxChars, out maxChars)) return false;
                        maxCharsSeen = true;
                        break;
                    default:
                        return false;
                }
            }

            if (!querySeen) return false;
            result = new SearchArguments(query, maxItems, maxChars);
            return true;
        }

        private static bool TryReadInteger(JsonElement value, int minimum, int maximum, out int result)
        {
            result = 0;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result)
                && result >= minimum && result <= maximum;
        }
    }

    private static int CountScalars(string value) => value.EnumerateRunes().Count();

    private static bool Fits(SearchPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, OutputJson).Length <= MaxOutputBytes;

    private static ToolResult Failure(string code) =>
        new(false, JsonSerializer.SerializeToElement(new { }, OutputJson), code)
        { ExecutionState = ToolExecutionState.NotExecuted };

    private sealed record SearchArguments(string Query, int MaxItems, int MaxChars);
    private sealed record SearchItem(string MemoryId, int Version, string Text, string? ProjectId);
    private sealed record SearchPayload(long Revision, IReadOnlyList<SearchItem> Items, bool Truncated);
}
