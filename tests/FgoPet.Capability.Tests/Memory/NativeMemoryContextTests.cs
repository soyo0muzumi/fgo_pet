using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Plugin.Memory;
using Xunit;

namespace FgoPet.Capability.Tests.Memory;

public sealed class NativeMemoryContextTests
{
    [Fact]
    public async Task Context_resolves_private_query_and_projects_only_enabled_records_for_host_scope()
    {
        var scope = new ToolScope("conversation-a", "role-a", "project-a");
        var reference = new ProtectedQueryReference("user-message-1", "fingerprint-1");
        var originalQuery = "private original user question";
        var resolver = new FakeResolver(originalQuery);
        var recall = new FakeRecall(17,
            Memory("generic", "role-a", "role wide fact", true, version: 3),
            Memory("current", "role-a", "project fact", true, projectId: "project-a", version: 7),
            Memory("disabled", "role-a", "disabled private fact", false),
            Memory("wrong-role", "role-b", "other role private fact", true),
            Memory("wrong-role-project", "role-b", "other role project private fact", true, projectId: "project-a"),
            Memory("wrong-project", "role-a", "other project private fact", true, projectId: "project-b"));
        var provider = new NativeMemoryContext(recall, resolver);

        var blocks = await provider.BuildAsync(Request(scope, reference), CancellationToken.None);

        Assert.Equal("confirmed-memory", provider.Id);
        Assert.False(provider.ProvidesTrustedInstructions);
        Assert.Equal(scope, resolver.SeenScope);
        Assert.Equal(reference, resolver.SeenReference);
        Assert.Equal(new MemoryScope("role-a", "project-a"), recall.LastScope);
        Assert.Equal(originalQuery, recall.LastQuery);
        Assert.Equal(8, recall.LastMaxItems);
        Assert.Equal(6000, recall.LastMaxChars);
        var block = Assert.Single(blocks);
        Assert.Equal(AgentContextKind.Data, block.Kind);
        Assert.Equal("confirmed-memory", block.Source);
        Assert.DoesNotContain(originalQuery, block.Content);

        using var document = JsonDocument.Parse(block.Content);
        Assert.Equal(17, document.RootElement.GetProperty("revision").GetInt64());
        var items = document.RootElement.GetProperty("memories").EnumerateArray().ToArray();
        Assert.Equal(new[] { ("generic", 3), ("current", 7) }, items.Select(item =>
            (item.GetProperty("memoryId").GetString()!, item.GetProperty("version").GetInt32())));
        Assert.DoesNotContain("disabled private fact", block.Content);
        Assert.DoesNotContain("other role private fact", block.Content);
        Assert.DoesNotContain("other role project private fact", block.Content);
        Assert.DoesNotContain("other project private fact", block.Content);
    }

    [Fact]
    public async Task Context_distinguishes_unavailable_query_from_no_matching_memories()
    {
        var scope = Scope();
        var reference = Reference();
        var unavailableRecall = new FakeRecall(3);
        var unavailableResolver = new FakeResolver(null);
        var unavailable = await new NativeMemoryContext(unavailableRecall, unavailableResolver)
            .BuildAsync(Request(scope, reference), CancellationToken.None);

        var marker = Assert.Single(unavailable);
        Assert.Equal(AgentContextKind.Data, marker.Kind);
        Assert.Equal("MEMORY_UNAVAILABLE", marker.Content);
        Assert.Equal(0, unavailableRecall.CallCount);

        var noHitRecall = new FakeRecall(3);
        var noHitResolver = new FakeResolver("resolved original message");
        var noHit = await new NativeMemoryContext(noHitRecall, noHitResolver)
            .BuildAsync(Request(scope, reference), CancellationToken.None);

        Assert.Empty(noHit);
        Assert.Equal(1, noHitRecall.CallCount);
    }

    [Fact]
    public async Task Context_returns_unavailable_without_resolving_or_recalling_when_disabled()
    {
        var recall = new FakeRecall(1, Memory("memory-1", "role-a", "private fact", true));
        var resolver = new FakeResolver("private original query");
        var provider = new NativeMemoryContext(recall, resolver, () => false);

        var blocks = await provider.BuildAsync(Request(), CancellationToken.None);

        var marker = Assert.Single(blocks);
        Assert.Equal(AgentContextKind.Data, marker.Kind);
        Assert.Equal("MEMORY_UNAVAILABLE", marker.Content);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, recall.CallCount);
    }

    [Fact]
    public async Task Context_discards_owner_results_if_disabled_during_retrieval()
    {
        var enabled = true;
        var recall = new FakeRecall(1, Memory("memory-1", "role-a", "private fact", true))
        {
            OnQuery = () => enabled = false
        };
        var resolver = new FakeResolver("private original query");
        var provider = new NativeMemoryContext(recall, resolver, () => enabled);

        var blocks = await provider.BuildAsync(Request(), CancellationToken.None);

        var marker = Assert.Single(blocks);
        Assert.Equal(AgentContextKind.Data, marker.Kind);
        Assert.Equal("MEMORY_UNAVAILABLE", marker.Content);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(1, recall.CallCount);
        Assert.DoesNotContain("private fact", marker.Content);
    }

    [Fact]
    public async Task Context_does_not_recall_if_disabled_during_query_resolution()
    {
        var enabled = true;
        var resolver = new FakeResolver("private original query") { OnResolve = () => enabled = false };
        var recall = new FakeRecall(1, Memory("memory-1", "role-a", "private fact", true));
        var provider = new NativeMemoryContext(recall, resolver, () => enabled);

        var blocks = await provider.BuildAsync(Request(), CancellationToken.None);

        var marker = Assert.Single(blocks);
        Assert.Equal(AgentContextKind.Data, marker.Kind);
        Assert.Equal("MEMORY_UNAVAILABLE", marker.Content);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, recall.CallCount);
        Assert.DoesNotContain("private original query", marker.Content);
    }

    [Fact]
    public async Task Context_returns_unavailable_if_disabled_after_projection()
    {
        var recall = new FakeRecall(1, Memory("memory-1", "role-a", "private fact", true));
        var enabledChecks = 0;
        var provider = new NativeMemoryContext(recall, new FakeResolver("private original query"),
            () => ++enabledChecks < 5);

        var blocks = await provider.BuildAsync(Request(), CancellationToken.None);

        var marker = Assert.Single(blocks);
        Assert.Equal(AgentContextKind.Data, marker.Kind);
        Assert.Equal("MEMORY_UNAVAILABLE", marker.Content);
        Assert.Equal(1, recall.CallCount);
        Assert.DoesNotContain("private fact", marker.Content);
        Assert.DoesNotContain("private original query", marker.Content);
    }

    [Fact]
    public async Task Context_returns_unavailable_data_marker_for_negative_revision_or_owner_failure()
    {
        var scope = Scope();
        var reference = Reference();
        var negativeRevision = await new NativeMemoryContext(
                new FakeRecall(-1, Memory("memory-1", "role-a", "fact", true)), new FakeResolver("query"))
            .BuildAsync(Request(scope, reference), CancellationToken.None);
        var ownerFailure = await new NativeMemoryContext(
                new FakeRecall(1, Memory("memory-1", "role-a", "fact", true))
                {
                    QueryFailure = new InvalidOperationException("private recall failure")
                }, new FakeResolver("query"))
            .BuildAsync(Request(scope, reference), CancellationToken.None);

        Assert.Equal("MEMORY_UNAVAILABLE", Assert.Single(negativeRevision).Content);
        Assert.Equal("MEMORY_UNAVAILABLE", Assert.Single(ownerFailure).Content);
        Assert.DoesNotContain("private recall failure", Assert.Single(ownerFailure).Content);
    }

    [Fact]
    public async Task Context_propagates_cancellation_and_does_not_recall_after_resolver_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new FakeResolver("resolved query") { OnResolve = cancellation.Cancel };
        var recall = new FakeRecall(1, Memory("memory-1", "role-a", "fact", true));
        var provider = new NativeMemoryContext(recall, resolver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.BuildAsync(Request(), cancellation.Token));

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, recall.CallCount);
    }

    [Fact]
    public async Task Context_propagates_cancellation_during_record_projection()
    {
        using var cancellation = new CancellationTokenSource();
        var items = new CancelOnReadList(cancellation,
            Memory("memory-1", "role-a", "fact", true),
            Memory("memory-2", "role-a", "another fact", true));
        var recall = new FakeRecall(1, items);
        var provider = new NativeMemoryContext(recall, new FakeResolver("query"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.BuildAsync(Request(), cancellation.Token));
    }

    [Fact]
    public async Task Context_bounds_record_count_whole_content_chars_and_utf8_bytes()
    {
        var scope = Scope();
        var reference = Reference();
        var many = new FakeRecall(5, Enumerable.Range(1, 12)
            .Select(index => Memory($"memory-{index}", "role-a", "fact", true)).ToArray());
        var boundedCount = await new NativeMemoryContext(many, new FakeResolver("query"))
            .BuildAsync(Request(scope, reference), CancellationToken.None);
        var countBlock = Assert.Single(boundedCount);
        using var countDocument = JsonDocument.Parse(countBlock.Content);
        Assert.InRange(countDocument.RootElement.GetProperty("memories").GetArrayLength(), 1, 8);
        Assert.True(countDocument.RootElement.GetProperty("truncated").GetBoolean());

        var large = new FakeRecall(6, Enumerable.Range(1, 8)
            .Select(index => Memory($"large-{index}", "role-a", new string('x', 2000), true)).ToArray());
        var boundedContent = await new NativeMemoryContext(large, new FakeResolver("query"))
            .BuildAsync(Request(scope, reference), CancellationToken.None);
        var contentBlock = Assert.Single(boundedContent);
        Assert.InRange(contentBlock.Content.Length, 1, 6000);
        Assert.InRange(Encoding.UTF8.GetByteCount(contentBlock.Content), 1, 32 * 1024);
        using var contentDocument = JsonDocument.Parse(contentBlock.Content);
        var memories = contentDocument.RootElement.GetProperty("memories").EnumerateArray().ToArray();
        Assert.InRange(memories.Length, 1, 8);
        Assert.All(memories, item => Assert.Equal(2000, item.GetProperty("text").GetString()!.Length));
        Assert.True(contentDocument.RootElement.GetProperty("truncated").GetBoolean());
    }

    private static AgentContextRequest Request(ToolScope? scope = null, ProtectedQueryReference? reference = null) =>
        new(scope ?? Scope(), "run-1", 1, reference ?? Reference(), 4000, ImmutableArray<ToolDescriptor>.Empty);

    private static ToolScope Scope() => new("conversation-1", "role-a", "project-a");

    private static ProtectedQueryReference Reference() => new("user-message-1", "fingerprint-1");

    private static StoredMemory Memory(string id, string roleId, string text, bool isEnabled,
        string? projectId = null, int version = 1) =>
        new(id, roleId, text, isEnabled, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            projectId: projectId, version: version);

    private sealed class FakeResolver(string? query) : IAgentQueryResolver
    {
        public int CallCount { get; private set; }
        public ToolScope? SeenScope { get; private set; }
        public ProtectedQueryReference? SeenReference { get; private set; }
        public Action? OnResolve { get; init; }

        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token)
        {
            CallCount++;
            SeenScope = scope;
            SeenReference = reference;
            OnResolve?.Invoke();
            return ValueTask.FromResult(query);
        }
    }

    private sealed class FakeRecall : IMemoryRecall
    {
        private readonly IReadOnlyList<StoredMemory> _items;

        public FakeRecall(long revision, params StoredMemory[] items)
        {
            Revision = revision;
            _items = items;
        }

        public FakeRecall(long revision, IReadOnlyList<StoredMemory> items)
        {
            Revision = revision;
            _items = items;
        }

        public long Revision { get; }
        public int CallCount { get; private set; }
        public MemoryScope? LastScope { get; private set; }
        public string? LastQuery { get; private set; }
        public int LastMaxItems { get; private set; }
        public int LastMaxChars { get; private set; }
        public Exception? QueryFailure { get; init; }
        public Action? OnQuery { get; init; }

        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000)
        {
            CallCount++;
            LastScope = scope;
            LastQuery = query;
            LastMaxItems = maxItems;
            LastMaxChars = maxChars;
            OnQuery?.Invoke();
            if (QueryFailure is not null) throw QueryFailure;
            return new MemoryRecallSnapshot(Revision, _items);
        }
    }

    private sealed class CancelOnReadList(CancellationTokenSource cancellation, params StoredMemory[] items)
        : IReadOnlyList<StoredMemory>
    {
        public int Count => items.Length;
        public StoredMemory this[int index]
        {
            get
            {
                var item = items[index];
                if (index == 0) cancellation.Cancel();
                return item;
            }
        }

        public IEnumerator<StoredMemory> GetEnumerator() => ((IEnumerable<StoredMemory>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => items.GetEnumerator();
    }
}
