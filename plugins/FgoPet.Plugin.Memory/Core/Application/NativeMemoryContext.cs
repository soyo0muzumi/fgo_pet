using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Memory;

/// <summary>Provides bounded confirmed-memory data resolved from the host's protected query reference.</summary>
public sealed class NativeMemoryContext : IAgentContextProvider
{
    private const int MaxItems = 8;
    private const int MaxChars = 6_000;
    private const int MaxInputItemsToInspect = 64;
    private const int MaxOutputBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMemoryRecall _recall;
    private readonly IAgentQueryResolver _queryResolver;
    private readonly Func<bool>? _enabled;

    public NativeMemoryContext(IMemoryRecall recall, IAgentQueryResolver queryResolver, Func<bool>? enabled = null)
    {
        _recall = recall ?? throw new ArgumentNullException(nameof(recall));
        _queryResolver = queryResolver ?? throw new ArgumentNullException(nameof(queryResolver));
        _enabled = enabled;
    }

    public string Id => "confirmed-memory";
    public bool ProvidesTrustedInstructions => false;

    public async ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsEnabled()) return Unavailable();
        if (request is null || request.Scope is null || request.Query is null)
            return Unavailable();

        string? originalUserQuery;
        try
        {
            originalUserQuery = await _queryResolver.ResolveAsync(request.Scope, request.Query, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            token.ThrowIfCancellationRequested();
            return Unavailable();
        }

        token.ThrowIfCancellationRequested();
        if (!IsEnabled()) return Unavailable();
        if (originalUserQuery is null) return Unavailable();

        MemoryScope scope;
        try { scope = new MemoryScope(request.Scope.RoleId, request.Scope.ProjectId); }
        catch (ArgumentException)
        {
            return Unavailable();
        }

        if (!IsEnabled()) return Unavailable();
        MemoryRecallSnapshot? snapshot;
        try
        {
            snapshot = _recall.Query(scope, originalUserQuery, MaxItems, MaxChars);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            token.ThrowIfCancellationRequested();
            return Unavailable();
        }

        token.ThrowIfCancellationRequested();
        if (!IsEnabled()) return Unavailable();
        var projected = Project(snapshot, scope, token);
        token.ThrowIfCancellationRequested();
        return IsEnabled() ? projected : Unavailable();
    }

    private bool IsEnabled()
    {
        try { return _enabled?.Invoke() ?? true; }
        catch (Exception) { return false; }
    }

    private IReadOnlyList<AgentContextBlock> Project(MemoryRecallSnapshot? snapshot, MemoryScope scope,
        CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (snapshot?.Items is null || snapshot.Revision < 0 || snapshot.Items.Count < 0)
                return Unavailable();

            var items = new List<MemoryContextItem>();
            var truncated = snapshot.Items.Count > MaxInputItemsToInspect;
            var itemsToInspect = Math.Min(snapshot.Items.Count, MaxInputItemsToInspect);

            for (var index = 0; index < itemsToInspect; index++)
            {
                token.ThrowIfCancellationRequested();
                var memory = snapshot.Items[index];
                token.ThrowIfCancellationRequested();
                if (memory is null || !memory.IsEnabled || memory.ServantId != scope.ServantId
                    || memory.ProjectId is not null && memory.ProjectId != scope.ProjectId)
                    continue;

                if (items.Count >= MaxItems)
                {
                    truncated = true;
                    break;
                }

                if (memory.Text is null)
                {
                    truncated = true;
                    continue;
                }

                var candidate = new MemoryContextItem(memory.MemoryId, memory.Version, memory.Text, memory.ProjectId);
                var preview = new MemoryContextPayload(snapshot.Revision, [.. items, candidate], Truncated: true);
                if (!Fits(preview))
                {
                    truncated = true;
                    continue;
                }

                items.Add(candidate);
                token.ThrowIfCancellationRequested();
            }

            token.ThrowIfCancellationRequested();
            if (items.Count == 0 && !truncated) return Array.Empty<AgentContextBlock>();

            var payload = new MemoryContextPayload(snapshot.Revision, items, truncated);
            if (!Fits(payload)) return Unavailable();
            return [new AgentContextBlock(Id, AgentContextKind.Data, JsonSerializer.Serialize(payload, OutputJson), 100)];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            token.ThrowIfCancellationRequested();
            return Unavailable();
        }
    }

    private static bool Fits(MemoryContextPayload payload)
    {
        var content = JsonSerializer.Serialize(payload, OutputJson);
        return content.Length <= MaxChars && StrictUtf8.GetByteCount(content) <= MaxOutputBytes;
    }

    private IReadOnlyList<AgentContextBlock> Unavailable() =>
        [new AgentContextBlock(Id, AgentContextKind.Data, "MEMORY_UNAVAILABLE", 1000)];

    private sealed record MemoryContextItem(string MemoryId, int Version, string Text, string? ProjectId);
    private sealed record MemoryContextPayload(long Revision, IReadOnlyList<MemoryContextItem> Memories, bool Truncated);
}
