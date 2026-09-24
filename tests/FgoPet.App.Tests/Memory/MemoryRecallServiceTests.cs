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
    public void No_match_only_returns_three_general_memories_and_never_partial_text()
    {
        var reader = new Reader { Items = Enumerable.Range(0, 10).Select(i => Item("m" + i, "a", null, new string('x', 100))).Append(Item("p", "a", "p", "项目事实")).ToArray() };
        var service = new MemoryRecallService(reader);
        Assert.Equal(3, service.Query(new("a", "p"), "unrelated").Items.Count);
        Assert.Single(service.Query(new("a", "p"), "unrelated", maxChars: 150).Items);
        Assert.Empty(service.Query(new("a", "p"), "unrelated", maxChars: 99).Items);
    }
    private static StoredMemory Item(string id, string role, string? project, string text) => new(id, role, text, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, projectId: project);
    private sealed class Reader : IMemorySnapshotReader
    {
        public long Revision = 1;
        public IReadOnlyList<StoredMemory> Items = [Item("general", "a", null, "喜欢安静"), Item("project", "a", "p", "导师面谈准备"), Item("other-project", "a", "q", "导师面谈秘密"), Item("other-role", "b", "p", "导师面谈秘密")];
        public MemoryRecallSnapshot ReadSnapshot(MemoryScope scope) => new(Revision, Items);
    }
}
