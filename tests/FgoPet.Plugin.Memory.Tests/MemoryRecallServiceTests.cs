using FgoPet.App.Memory;
using FgoPet.Core.Memory;
using Xunit;

namespace FgoPet.App.Tests.Memory;

public sealed class MemoryRecallServiceTests
{
    [Fact]
    public void Scope_matching_and_revision_are_read_for_every_query()
    {
        var reader = new Reader();
        var service = new MemoryRecallService(reader);
        var first = service.Query(new("a", "p"), "导师面谈");
        Assert.Equal("project", Assert.Single(first.Items).MemoryId);
        reader.Revision++;
        reader.Items = [Item("new", "a", "p", "导师面谈改期")];
        var second = service.Query(new("a", "p"), "导师面谈");
        Assert.True(second.Revision > first.Revision);
        Assert.Equal("new", Assert.Single(second.Items).MemoryId);
    }
    [Fact]
    public void No_match_returns_no_memories_on_the_strict_path()
    {
        var reader = new Reader { Items = Enumerable.Range(0, 10).Select(i => Item("m" + i, "a", null, new string('x', 100))).Append(Item("p", "a", "p", "项目事实")).ToArray() };
        var service = new MemoryRecallService(reader);
        // QueryStrict is what prompt injection, native context and memory.search use.
        // A query with no matching term must stay empty instead of being padded with
        // unrelated general memories.
        Assert.Empty(service.QueryStrict(new("a", "p"), "unrelated").Items);
        Assert.Empty(service.QueryStrict(new("a", "p"), "unrelated", maxChars: 150).Items);
        Assert.Empty(service.QueryStrict(new("a", "p"), "unrelated", maxChars: 99).Items);
    }
    [Fact]
    public void No_match_on_the_background_path_keeps_bounded_general_memories()
    {
        // Query keeps the general background because the extraction prompt feeds it as
        // `confirmed` context to decide whether the user is correcting an existing memory.
        var reader = new Reader { Items = Enumerable.Range(0, 10).Select(i => Item("m" + i, "a", null, new string('x', 100))).Append(Item("p", "a", "p", "项目事实")).ToArray() };
        var service = new MemoryRecallService(reader);
        Assert.Equal(3, service.Query(new("a", "p"), "unrelated").Items.Count);
        Assert.Single(service.Query(new("a", "p"), "unrelated", maxChars: 150).Items);
        Assert.Empty(service.Query(new("a", "p"), "unrelated", maxChars: 99).Items);
    }
    [Fact]
    public void Strict_path_still_returns_actual_matches()
    {
        var reader = new Reader();
        var service = new MemoryRecallService(reader);
        var hit = service.QueryStrict(new("a", "p"), "导师面谈");
        Assert.Equal("project", Assert.Single(hit.Items).MemoryId);
    }
    [Fact]
    public void Strict_path_keeps_scope_and_capacity_semantics()
    {
        var reader = new Reader();
        var service = new MemoryRecallService(reader);

        // Scope isolation is unchanged: other project / other role never leak.
        var scoped = service.QueryStrict(new("a", "p"), "导师面谈");
        Assert.Equal("project", Assert.Single(scoped.Items).MemoryId);

        // Capacity caps are unchanged.
        Assert.Empty(service.QueryStrict(new("a", "p"), "导师面谈", maxChars: 1).Items);
        Assert.Empty(service.QueryStrict(new("a", "p"), "导师面谈", maxItems: 0).Items);

        // Disabled memories are still filtered out before matching.
        reader.Items = [Item("disabled", "a", "p", "导师面谈准备", isEnabled: false)];
        Assert.Empty(service.QueryStrict(new("a", "p"), "导师面谈").Items);
    }
    private static StoredMemory Item(string id, string role, string? project, string text, bool isEnabled = true) => new(id, role, text, isEnabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, projectId: project);
    private sealed class Reader : IMemorySnapshotReader
    {
        public long Revision = 1;
        public IReadOnlyList<StoredMemory> Items = [Item("general", "a", null, "喜欢安静"), Item("project", "a", "p", "导师面谈准备"), Item("other-project", "a", "q", "导师面谈秘密"), Item("other-role", "b", "p", "导师面谈秘密")];
        public MemoryRecallSnapshot ReadSnapshot(MemoryScope scope) => new(Revision, Items);
    }
}
