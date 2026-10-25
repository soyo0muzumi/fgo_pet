using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FgoPet.App.Memory;
using FgoPet.Core.Memory;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Plugin.Memory;
using Xunit;

namespace FgoPet.Capability.Tests.Memory;

public sealed class NativeMemoryProvenanceTests
{
    private static readonly ToolScope Scope = new("current", "role", "project");
    private static readonly DateTimeOffset Occurred = DateTimeOffset.Parse("2026-10-01T02:03:04Z");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_native_outputs_preserve_provenance_and_confirmation(bool sourceAvailable)
    {
        var memory = Memory("memory", sourceAvailable: sourceAvailable);
        var recall = new SnapshotRecall(new(42, [memory]));
        var context = await Context(new NativeMemoryContext(recall, new Resolver()));
        var search = await Search(Assert.Single(NativeMemoryTools.Create(recall)));
        foreach (var item in new[] { context.GetProperty("memories")[0], search.GetProperty("items")[0] })
        {
            Assert.Equal("memory", item.GetProperty("memoryId").GetString());
            Assert.Equal(3, item.GetProperty("version").GetInt32());
            Assert.Equal("project", item.GetProperty("projectId").GetString());
            Assert.Equal("AssistantSuggestion", item.GetProperty("sourceKind").GetString());
            Assert.Equal(Occurred, item.GetProperty("sourceOccurredAtUtc").GetDateTimeOffset());
            Assert.Equal("origin", item.GetProperty("sourceConversationId").GetString());
            Assert.Equal("origin-message", item.GetProperty("sourceMessageId").GetString());
            Assert.Equal(sourceAvailable, item.GetProperty("sourceAvailable").GetBoolean());
            Assert.Equal("UserReviewed", item.GetProperty("confirmation").GetString());
            Assert.DoesNotContain("fixture-fingerprint", item.GetRawText());
            Assert.DoesNotContain("fixture-private-label", item.GetRawText());
        }
    }

    [Fact]
    public async Task Legacy_memory_explicitly_marks_unknown_source_without_inventing_origin()
    {
        var memory = new StoredMemory("legacy", "role", "fixture legacy", true, Occurred, Occurred,
            projectId: "project", sourceAvailable: true);
        var recall = new SnapshotRecall(new(9, [memory]));
        var context = await Context(new NativeMemoryContext(recall, new Resolver()));
        var search = await Search(Assert.Single(NativeMemoryTools.Create(recall)));
        foreach (var item in new[] { context.GetProperty("memories")[0], search.GetProperty("items")[0] })
        {
            Assert.Equal("UnknownLegacy", item.GetProperty("sourceKind").GetString());
            Assert.False(item.GetProperty("sourceAvailable").GetBoolean());
            Assert.Equal("UserReviewed", item.GetProperty("confirmation").GetString());
            Assert.False(item.TryGetProperty("sourceOccurredAtUtc", out _));
            Assert.False(item.TryGetProperty("sourceConversationId", out _));
            Assert.False(item.TryGetProperty("sourceMessageId", out _));
        }
    }

    [Fact]
    public async Task Added_metadata_keeps_whole_records_and_existing_limits()
    {
        var records = Enumerable.Range(1, 8).Select(index => Memory("memory-" + index, new string('猫', 2000))).ToArray();
        var recall = new SnapshotRecall(new(8, records));
        var context = await Context(new NativeMemoryContext(recall, new Resolver()));
        var search = await Search(Assert.Single(NativeMemoryTools.Create(recall)));
        Assert.InRange(context.GetRawText().Length, 1, 6000);
        Assert.InRange(Encoding.UTF8.GetByteCount(context.GetRawText()), 1, 32 * 1024);
        Assert.InRange(Encoding.UTF8.GetByteCount(search.GetRawText()), 1, 48 * 1024);
        Assert.True(context.GetProperty("truncated").GetBoolean());
        Assert.True(search.GetProperty("truncated").GetBoolean());
        foreach (var item in context.GetProperty("memories").EnumerateArray().Concat(search.GetProperty("items").EnumerateArray()))
        {
            Assert.Equal(new string('猫', 2000), item.GetProperty("text").GetString());
            Assert.Equal("AssistantSuggestion", item.GetProperty("sourceKind").GetString());
        }
        Assert.Equal(3, search.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Reused_native_readers_see_sqlite_correction_and_disable_at_new_revision()
    {
        using var fixture = new MemoryFixture();
        var original = fixture.Approve("origin", "fixture tea");
        var recall = new MemoryRecallService(fixture.Repository);
        var contextProvider = new NativeMemoryContext(recall, new Resolver());
        var searchProvider = Assert.Single(NativeMemoryTools.Create(recall));
        var before = await Context(contextProvider);
        var beforeSearch = await Search(searchProvider);
        var oldRevision = before.GetProperty("revision").GetInt64();
        Assert.Equal(oldRevision, beforeSearch.GetProperty("revision").GetInt64());

        var corrected = fixture.Approve("correction", "fixture coffee", original);
        var after = await Context(contextProvider);
        var afterSearch = await Search(searchProvider);
        Assert.True(after.GetProperty("revision").GetInt64() > oldRevision);
        Assert.Equal(after.GetProperty("revision").GetInt64(), afterSearch.GetProperty("revision").GetInt64());
        foreach (var item in new[] { after.GetProperty("memories")[0], afterSearch.GetProperty("items")[0] })
        {
            Assert.Equal(original.MemoryId, item.GetProperty("memoryId").GetString());
            Assert.Equal(2, item.GetProperty("version").GetInt32());
            Assert.Equal("fixture coffee", item.GetProperty("text").GetString());
            Assert.Equal("correction", item.GetProperty("sourceConversationId").GetString());
            Assert.Equal("correction-message", item.GetProperty("sourceMessageId").GetString());
            Assert.True(item.GetProperty("sourceAvailable").GetBoolean());
        }

        fixture.Repository.ReviewMemory(corrected.MemoryId, "role", MemoryReviewAction.Disable, null, Occurred.AddDays(1), corrected.Version);
        var disabled = await Search(searchProvider);
        Assert.True(disabled.GetProperty("revision").GetInt64() > afterSearch.GetProperty("revision").GetInt64());
        Assert.Empty(disabled.GetProperty("items").EnumerateArray());
        Assert.Empty(await contextProvider.BuildAsync(Request(), default));
    }

    private static StoredMemory Memory(string id, string text = "fixture preference", bool sourceAvailable = true) =>
        new(id, "role", text, true, Occurred, Occurred, projectId: "project", version: 3,
            source: new(new("role", "project"), "origin", "origin-message", "fixture-fingerprint", Occurred,
                MemoryEvidenceKind.AssistantSuggestion, "fixture-private-label"), sourceAvailable: sourceAvailable);
    private static AgentContextRequest Request() => new(Scope, "run", 1, new("current-message", "fixture-query"), 6000, ImmutableArray<ToolDescriptor>.Empty);
    private static async Task<JsonElement> Context(NativeMemoryContext provider)
    {
        using var document = JsonDocument.Parse(Assert.Single(await provider.BuildAsync(Request(), default)).Content);
        return document.RootElement.Clone();
    }
    private static async Task<JsonElement> Search(IToolProvider provider)
    {
        var result = await provider.InvokeAsync(new(Scope, JsonSerializer.SerializeToElement(new { query = "fixture" })), default);
        Assert.True(result.Success);
        return result.Payload;
    }
    private sealed class Resolver : IAgentQueryResolver
    {
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token) => ValueTask.FromResult<string?>("fixture");
    }
    private sealed class SnapshotRecall(MemoryRecallSnapshot snapshot) : IMemoryRecall
    {
        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) => snapshot;
    }
    private sealed class MemoryFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "native-memory-provenance-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly RuntimeDatabase _database;
        public SqliteMemoryRepository Repository { get; }
        public MemoryFixture()
        {
            _database = new(_path, pooling: false);
            new RuntimeDatabaseMigrator(_database).Migrate();
            Repository = new(_database);
            Repository.StartSession();
        }
        public StoredMemory Approve(string source, string text, StoredMemory? replaces = null)
        {
            using (var connection = _database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO conversations(conversation_id,servant_id,project_id,created_at_utc,updated_at_utc,status)
                    VALUES($source,'role','project','2026-10-01','2026-10-01','active');
                    INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc)
                    VALUES($message,$source,'role',1,'user','fixture','completed','2026-10-01');
                    """;
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$message", source + "-message");
                command.ExecuteNonQuery();
            }
            var ticket = Repository.Begin(new(new("role", "project"), source, source + "-message", "fixture-" + source,
                Occurred, MemoryEvidenceKind.UserStatement));
            var staged = Repository.Stage(ticket, [new(text, replaces?.MemoryId, replaces?.Version)]);
            Assert.Equal(MemoryStageStatus.Staged, staged.Status);
            return Repository.ReviewCandidate(Assert.Single(staged.CandidateIds), "role", MemoryReviewAction.Approve, null, Occurred)!;
        }
        public void Dispose()
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
}
