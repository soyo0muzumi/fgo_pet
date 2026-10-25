using System.IO;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Conversation;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NativeCompactedHistoryToolsTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"fgo-history-tool-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase database;
    private readonly SqliteConversationRepository conversations;
    private readonly IReadOnlyList<IToolProvider> tools;
    private static readonly ToolScope Scope = new("c", "mash", "p");

    public NativeCompactedHistoryToolsTests()
    {
        database = new(path, pooling: false); new RuntimeDatabaseMigrator(database).Migrate();
        conversations = new(database);
        var key = new ContentContextKey("mash", "test", "1", "default", "1", "1");
        conversations.CreateConversation("c", "mash", key, DateTimeOffset.UtcNow, "p", "P");
        conversations.Append(new("u", "c", "mash", ChatMessageRole.User, "😀 omitted exact detail", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, key, 1));
        conversations.Append(new("a", "c", "mash", ChatMessageRole.Assistant, "unfinished", ChatMessageStatus.Failed, DateTimeOffset.UtcNow, key, 2));
        conversations.Append(new("tail", "c", "mash", ChatMessageRole.User, "now", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, key, 3));
        var contexts = new SqliteConversationContextStore(database);
        Assert.True(contexts.TryCommit(new(contexts.Read(new("mash", "p"), "c"),
            new("s", "c", "mash", "Summary omits the source detail", 2, "a", key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("test", "endpoint", "m", "1"), 100, 50)));
        tools = NativeCompactedHistoryTools.Create(new SqliteCompactedHistoryRepository(database));
    }

    [Fact]
    public async Task Search_then_read_exposes_original_source_and_unicode_continuation()
    {
        var search = await Invoke("history.search", "{\"query\":\"exact\",\"maxChars\":3}");
        Assert.True(search.Success);
        var hit = Assert.Single(search.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal("u", hit.GetProperty("messageId").GetString());
        Assert.Equal("user", hit.GetProperty("role").GetString());
        Assert.Equal("completed", hit.GetProperty("status").GetString());
        var read = await Invoke("history.read", "{\"messageId\":\"u\",\"maxChars\":1}");
        Assert.True(read.Success);
        var item = Assert.Single(read.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal("😀", item.GetProperty("text").GetString());
        Assert.Equal(1, item.GetProperty("nextOffset").GetInt32());
        var failed = await Invoke("history.read", "{\"messageId\":\"a\"}");
        Assert.Equal("failed", Assert.Single(failed.Payload.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(search.Payload.GetRawText()) <= 48 * 1024);
        Assert.All(tools, x => Assert.Equal(ToolEffect.ReadOnly, x.Descriptor.Effect));
    }

    [Theory]
    [InlineData("history.search", "{\"query\":\"x\",\"conversationId\":\"foreign\"}")]
    [InlineData("history.search", "{\"query\":\"x\",\"query\":\"y\"}")]
    [InlineData("history.search", "{\"query\":\"x\",\"maxItems\":9}")]
    [InlineData("history.search", "{\"query\":\"x\",\"maxChars\":6001}")]
    [InlineData("history.search", "{\"query\":\" \"}")]
    [InlineData("history.search", "{\"query\":\"x\",\"afterSequence\":-1}")]
    [InlineData("history.read", "{\"messageId\":\"u\",\"roleId\":\"foreign\"}")]
    [InlineData("history.read", "{\"messageId\":\"u\",\"projectId\":\"foreign\"}")]
    [InlineData("history.read", "{\"messageId\":\"u\",\"offset\":-1}")]
    [InlineData("history.read", "{\"messageId\":\"u\",\"offset\":1.5}")]
    [InlineData("history.read", "{\"messageId\":\"u\",\"offset\":0,\"offset\":1}")]
    public async Task Invalid_or_forged_arguments_are_rejected(string name, string json)
    {
        var result = await Invoke(name, json);
        Assert.False(result.Success);
        Assert.Equal("TOOL_INVALID_ARGUMENTS", result.ErrorCode);
    }

    [Fact]
    public async Task Dense_emoji_original_is_reconstructed_across_byte_bounded_read_pages()
    {
        var original = AddDenseEmojiMessages();
        var reconstructed = new StringBuilder();
        var offset = 0;
        var pages = 0;
        while (true)
        {
            var result = await Invoke("history.read", JsonSerializer.Serialize(new { messageId = "dense4", offset }));
            Assert.True(result.Success, result.ErrorCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(result.Payload.GetRawText()), 1, 48 * 1024);
            var item = Assert.Single(result.Payload.GetProperty("items").EnumerateArray());
            Assert.Equal(offset, item.GetProperty("offset").GetInt32());
            var text = item.GetProperty("text").GetString()!;
            Assert.NotEmpty(text);
            Assert.DoesNotContain('\uFFFD', text);
            reconstructed.Append(text);
            pages++;
            if (!item.TryGetProperty("nextOffset", out var next)) break;
            Assert.True(result.Payload.GetProperty("truncated").GetBoolean());
            Assert.Equal(offset + text.EnumerateRunes().Count(), next.GetInt32());
            offset = next.GetInt32();
            Assert.True(pages < 10);
        }
        Assert.True(pages > 1);
        Assert.Equal(original, reconstructed.ToString());
    }

    [Fact]
    public async Task Byte_bounded_search_preserves_message_and_search_continuations()
    {
        AddDenseEmojiMessages();
        var ids = new List<string>();
        var afterSequence = 0;
        for (var pageIndex = 0; pageIndex < 4; pageIndex++)
        {
            var result = await Invoke("history.search", JsonSerializer.Serialize(new { query = "🧭", afterSequence }));
            Assert.True(result.Success, result.ErrorCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(result.Payload.GetRawText()), 1, 48 * 1024);
            var items = result.Payload.GetProperty("items").EnumerateArray().ToArray();
            Assert.NotEmpty(items);
            foreach (var item in items)
            {
                ids.Add(item.GetProperty("messageId").GetString()!);
                Assert.Equal(item.GetProperty("offset").GetInt32() + item.GetProperty("text").GetString()!.EnumerateRunes().Count(),
                    item.GetProperty("nextOffset").GetInt32());
            }
            if (!result.Payload.TryGetProperty("nextSequence", out var next)) break;
            Assert.Equal(items[^1].GetProperty("sequence").GetInt32(), next.GetInt32());
            Assert.True(next.GetInt32() > afterSequence);
            afterSequence = next.GetInt32();
        }
        Assert.Equal(new[] { "dense4", "dense5", "dense6" }, ids);
    }

    private string AddDenseEmojiMessages()
    {
        var original = string.Concat(Enumerable.Repeat("🧭😀", 2500));
        var key = new ContentContextKey("mash", "test", "1", "default", "1", "1");
        for (var sequence = 4; sequence <= 6; sequence++)
            conversations.Append(new("dense" + sequence, "c", "mash",
                sequence % 2 == 0 ? ChatMessageRole.Assistant : ChatMessageRole.User,
                original, ChatMessageStatus.Completed, DateTimeOffset.UtcNow, key, sequence));
        conversations.Append(new("next", "c", "mash", ChatMessageRole.User, "next turn", ChatMessageStatus.Completed, DateTimeOffset.UtcNow, key, 7));
        var contexts = new SqliteConversationContextStore(database);
        Assert.True(contexts.TryCommit(new(contexts.Read(new("mash", "p"), "c"),
            new("dense-summary", "c", "mash", "Compressed dense history", 6, "dense6", key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new("test", "endpoint", "m", "1"), 1000, 50)));
        return original;
    }

    [Fact]
    public async Task Unavailable_scope_deleted_source_and_cancelled_calls_return_safe_codes()
    {
        var tool = tools.Single(x => x.Descriptor.Name == "history.read");
        using var arguments = JsonDocument.Parse("{\"messageId\":\"u\"}");
        var unavailable = await tool.InvokeAsync(new(Scope with { ProjectId = "other" }, arguments.RootElement), default);
        Assert.Equal("HISTORY_UNAVAILABLE", unavailable.ErrorCode);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal("TOOL_CANCELLED", (await tool.InvokeAsync(new(Scope, arguments.RootElement), cancellation.Token)).ErrorCode);
        conversations.DeleteConversation("c", "mash");
        Assert.Equal("HISTORY_UNAVAILABLE", (await Invoke("history.read", "{\"messageId\":\"u\"}")).ErrorCode);
    }

    private async Task<ToolResult> Invoke(string name, string json)
    {
        using var arguments = JsonDocument.Parse(json);
        return await tools.Single(x => x.Descriptor.Name == name).InvokeAsync(new(Scope, arguments.RootElement), default);
    }

    [Theory]
    [InlineData("history.search", "{\"query\":\"exact\"}")]
    [InlineData("history.read", "{\"messageId\":\"u\"}")]
    public async Task Host_scope_revocation_before_delivery_discards_retrieved_originals(string name, string json)
    {
        var calls = 0;
        var guarded = NativeCompactedHistoryTools.Create(new SqliteCompactedHistoryRepository(database), scope =>
        {
            Assert.Equal(Scope, scope);
            return ++calls == 1;
        }).Single(tool => tool.Descriptor.Name == name);
        using var arguments = JsonDocument.Parse(json);
        var result = await guarded.InvokeAsync(new(Scope, arguments.RootElement), default);
        Assert.False(result.Success);
        Assert.Equal("HISTORY_SCOPE_DENIED", result.ErrorCode);
        Assert.Equal("{}", result.Payload.GetRawText());
        Assert.Equal(2, calls);
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
}
