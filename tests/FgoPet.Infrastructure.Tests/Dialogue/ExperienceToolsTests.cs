using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class ExperienceToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-experience-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
    private static readonly ToolScope Live = new("live", "mash", "project-a");

    public ExperienceToolsTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
        Seed("live", "mash", "project-a", "当前输入");
    }

    [Fact]
    public async Task Search_returns_an_original_scoped_message_and_read_preserves_emoji_coverage()
    {
        Seed("history", "mash", "project-a", "计划😀修订");
        Seed("other-project", "mash", "project-b", "计划另一个项目");
        Seed("other-role", "other", "project-a", "计划另一个角色");
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);

        var search = await Invoke(plugin, "experience.search", Live, new { query = "计划" });
        Assert.True(search.Success);
        var item = Assert.Single(search.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal("history-m1", item.GetProperty("messageId").GetString());
        Assert.Equal("user", item.GetProperty("speaker").GetString());
        Assert.Equal(Now, item.GetProperty("occurredAtUtc").GetDateTimeOffset());
        Assert.True(search.Payload.GetProperty("coverage").GetProperty("nonExhaustive").GetBoolean());
        var sourceId = item.GetProperty("sourceId").GetString();
        var sourceVersion = item.GetProperty("sourceVersion").GetString();
        Assert.Matches("^[A-F0-9]{64}$", sourceVersion!);

        var read = await Invoke(plugin, "experience.read", Live,
            new { sourceId, sourceVersion, offset = 2, maxChars = 1 });
        Assert.True(read.Success);
        Assert.Equal("😀", read.Payload.GetProperty("text").GetString());
        var coverage = read.Payload.GetProperty("coverage");
        Assert.Equal("unicode_scalar", coverage.GetProperty("offsetUnit").GetString());
        Assert.Equal(2, coverage.GetProperty("offset").GetInt32());
        Assert.Equal(1, coverage.GetProperty("returnedCharacters").GetInt32());
        Assert.Equal(5, coverage.GetProperty("totalCharacters").GetInt32());
        Assert.False(coverage.GetProperty("conversationComplete").GetBoolean());
        Assert.False(coverage.GetProperty("fullMessageInPage").GetBoolean());
        Assert.Equal(3, read.Payload.GetProperty("nextOffset").GetInt32());
    }

    [Fact]
    public async Task Changed_original_text_invalidates_a_previously_returned_version()
    {
        Seed("history", "mash", "project-a", "计划原文");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        Execute("UPDATE chat_messages SET text='计划修订后' WHERE message_id='history-m1'");
        var result = await Invoke(plugin, "experience.read", Live, reference);
        AssertFailure(result, "EXPERIENCE_SOURCE_CHANGED");
    }

    [Fact]
    public async Task Deleted_source_is_different_from_an_unknown_reference()
    {
        Seed("history", "mash", "project-a", "计划原文");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        _conversations.DeleteConversation("history", "mash");
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SOURCE_DELETED");
        AssertFailure(await Invoke(plugin, "experience.read", Live,
            new { sourceId = "experience-unknown", sourceVersion = new string('A', 64) }), "EXPERIENCE_SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task A_reference_cannot_be_reused_in_another_host_scope()
    {
        Seed("history", "mash", "project-a", "计划原文");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        AssertFailure(await Invoke(plugin, "experience.read", new("other-live", "mash", "project-a"), reference),
            "EXPERIENCE_SCOPE_DENIED");
        AssertFailure(await Invoke(plugin, "experience.read", new("live", "mash", "project-b"), reference),
            "EXPERIENCE_SCOPE_DENIED");
        AssertFailure(await Invoke(plugin, "experience.read", new("live", "other", "project-a"), reference),
            "EXPERIENCE_SCOPE_DENIED");
    }

    [Fact]
    public async Task Empty_bounded_search_and_temporary_storage_failure_are_different()
    {
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var empty = await Invoke(plugin, "experience.search", Live, new { query = "没有匹配材料" });
        Assert.True(empty.Success);
        Assert.Equal("empty", empty.Payload.GetProperty("status").GetString());
        Assert.Empty(empty.Payload.GetProperty("items").EnumerateArray());
        Execute("DROP TABLE chat_message_search");
        AssertFailure(await Invoke(plugin, "experience.search", Live, new { query = "没有匹配材料" }),
            "EXPERIENCE_UNAVAILABLE");
    }

    [Fact]
    public async Task Revoked_scope_discards_a_read_result_after_the_owner_was_called()
    {
        Seed("history", "mash", "project-a", "计划原文");
        var remainingChecks = int.MaxValue;
        await using var plugin = CreatePlugin(_ => remainingChecks-- > 0);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        remainingChecks = 1;
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SCOPE_DENIED");
    }

    [Fact]
    public async Task Revoked_scope_discards_search_results_instead_of_retrying()
    {
        Seed("history", "mash", "project-a", "计划原文");
        var remainingChecks = 1;
        await using var plugin = CreatePlugin(_ => remainingChecks-- > 0);
        await plugin.StartAsync(default);
        AssertFailure(await Invoke(plugin, "experience.search", Live, new { query = "计划" }), "EXPERIENCE_SCOPE_DENIED");
    }

    [Fact]
    public async Task Completed_assistant_source_keeps_its_speaker_and_partial_output_is_excluded()
    {
        Seed("history", "mash", "project-a", "用户记录");
        Append("history", ChatMessageRole.Assistant, "计划尚未完成", ChatMessageStatus.Cancelled, 2);
        Append("history", ChatMessageRole.Assistant, "计划助手原话", ChatMessageStatus.Completed, 3);
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var search = await Invoke(plugin, "experience.search", Live, new { query = "计划" });
        Assert.True(search.Success);
        var item = Assert.Single(search.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal("assistant", item.GetProperty("speaker").GetString());
        Assert.Equal("history-m3", item.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task Read_pages_reconstruct_one_message_without_claiming_complete_conversation_coverage()
    {
        Seed("history", "mash", "project-a", "计划😀修订");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        var offset = 0;
        var text = string.Empty;
        do
        {
            var page = await Invoke(plugin, "experience.read", Live,
                new { reference.sourceId, reference.sourceVersion, offset, maxChars = 2 });
            Assert.True(page.Success);
            Assert.False(page.Payload.GetProperty("coverage").GetProperty("conversationComplete").GetBoolean());
            text += page.Payload.GetProperty("text").GetString();
            var next = page.Payload.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.True(next.GetInt32() > offset);
            offset = next.GetInt32();
        } while (true);
        Assert.Equal("计划😀修订", text);
    }

    [Fact]
    public async Task Short_query_limits_are_reported_and_do_not_claim_missing_old_history()
    {
        Seed("old", "mash", "project-a", "计划旧材料");
        for (var index = 0; index < 12; index++) Seed($"recent-{index:D2}", "mash", "project-a", "其他材料");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var bounded = await Invoke(plugin, "experience.search", Live, new { query = "计划" });
        Assert.True(bounded.Success);
        Assert.Empty(bounded.Payload.GetProperty("items").EnumerateArray());
        var coverage = bounded.Payload.GetProperty("coverage");
        Assert.Equal("recent_literal", coverage.GetProperty("strategy").GetString());
        Assert.Equal(10, coverage.GetProperty("maxConversationsSearched").GetInt32());
        Assert.Equal(200, coverage.GetProperty("maxMessagesSearched").GetInt32());
        Assert.True(coverage.GetProperty("nonExhaustive").GetBoolean());
        Assert.False(coverage.GetProperty("searchPaginationAvailable").GetBoolean());
        var longer = await Invoke(plugin, "experience.search", Live, new { query = "计划旧材料" });
        Assert.True(longer.Success);
        Assert.Single(longer.Payload.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Wrong_version_and_invalid_offsets_never_return_original_text()
    {
        Seed("history", "mash", "project-a", "计划原文");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        AssertFailure(await Invoke(plugin, "experience.read", Live,
            new { reference.sourceId, sourceVersion = new string('A', 64) }), "EXPERIENCE_SOURCE_CHANGED");
        AssertFailure(await Invoke(plugin, "experience.read", Live,
            new { reference.sourceId, reference.sourceVersion, offset = 100 }), "TOOL_INVALID_ARGUMENTS");
    }

    [Fact]
    public async Task Model_arguments_cannot_supply_scope_or_duplicate_query_fields()
    {
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        AssertFailure(await Invoke(plugin, "experience.search", Live,
            new { query = "计划", roleId = "other" }), "TOOL_INVALID_ARGUMENTS");
        using var duplicate = JsonDocument.Parse("{\"query\":\"计划\",\"query\":\"其他\"}");
        var search = Assert.Single(plugin.Contributions.Tools.Where(tool => tool.Descriptor.Name == "experience.search"));
        AssertFailure(await search.InvokeAsync(new(Live, duplicate.RootElement.Clone()), default), "TOOL_INVALID_ARGUMENTS");
    }

    [Fact]
    public async Task Scope_denial_happens_before_an_unavailable_storage_read()
    {
        await using var plugin = CreatePlugin(_ => false);
        await plugin.StartAsync(default);
        Execute("DROP TABLE chat_message_search");
        AssertFailure(await Invoke(plugin, "experience.search", Live, new { query = "计划材料" }), "EXPERIENCE_SCOPE_DENIED");
    }

    [Fact]
    public async Task Emoji_query_limit_agrees_with_the_runtime_schema_at_128_scalars()
    {
        var query = string.Concat(Enumerable.Repeat("😀", 128));
        Seed("history", "mash", "project-a", query + "原文");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var search = Assert.Single(plugin.Contributions.Tools.Where(tool => tool.Descriptor.Name == "experience.search"));
        var arguments = JsonSerializer.SerializeToElement(new { query });
        Assert.True(ToolArgumentsValidator.Validate(search.Descriptor.Parameters, arguments, out _));
        var result = await search.InvokeAsync(new(Live, arguments), default);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Single(result.Payload.GetProperty("items").EnumerateArray());
        var tooLong = JsonSerializer.SerializeToElement(new { query = query + "😀" });
        Assert.False(ToolArgumentsValidator.Validate(search.Descriptor.Parameters, tooLong, out _));
        AssertFailure(await search.InvokeAsync(new(Live, tooLong), default), "TOOL_INVALID_ARGUMENTS");
    }

    [Fact]
    public async Task Source_version_matches_the_existing_original_message_owner_convention()
    {
        Seed("history", "mash", "project-a", "计划😀原始话语");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        var owner = new ConversationSourceReader(_conversations).Read(new("history", "mash", "project-a"),
            "history-m1", ConversationSourceKind.User);
        Assert.Equal(owner.Fingerprint, reference.sourceVersion);
    }

    [Theory]
    [InlineData("计划\0😀修订")]
    [InlineData("\0计划😀修订")]
    [InlineData("计划😀修订\0")]
    public async Task Search_and_read_preserve_a_publicly_saved_nul_original(string original)
    {
        Seed("history", "mash", "project-a", original);
        Assert.Equal(original, Assert.Single(_conversations.LoadMessages("history", "mash")).Text);
        var owner = new ConversationSourceReader(_conversations).Read(new("history", "mash", "project-a"),
            "history-m1", ConversationSourceKind.User);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);

        var search = await Invoke(plugin, "experience.search", Live, new { query = "计划" });
        Assert.True(search.Success, search.ErrorCode);
        var item = Assert.Single(search.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal(original, item.GetProperty("excerpt").GetString());
        Assert.False(item.GetProperty("isTruncated").GetBoolean());
        Assert.Equal(owner.Fingerprint, item.GetProperty("sourceVersion").GetString());
        var read = await Invoke(plugin, "experience.read", Live, new
        {
            sourceId = item.GetProperty("sourceId").GetString(),
            sourceVersion = item.GetProperty("sourceVersion").GetString()
        });

        Assert.True(read.Success, read.ErrorCode);
        Assert.Equal(original, read.Payload.GetProperty("text").GetString());
        Assert.Equal(owner.Fingerprint, read.Payload.GetProperty("sourceVersion").GetString());
        Assert.Equal(JsonValueKind.Null, read.Payload.GetProperty("nextOffset").ValueKind);
        var coverage = read.Payload.GetProperty("coverage");
        Assert.Equal(6, coverage.GetProperty("totalCharacters").GetInt32());
        Assert.Equal(6, coverage.GetProperty("returnedCharacters").GetInt32());
        Assert.True(coverage.GetProperty("fullMessageInPage").GetBoolean());
        Assert.True(coverage.GetProperty("atMessageEnd").GetBoolean());
        Assert.False(coverage.GetProperty("conversationComplete").GetBoolean());
    }

    [Fact]
    public async Task Read_pages_continue_past_nul_at_unicode_scalar_boundaries()
    {
        const string original = "计划\0😀修订";
        Seed("history", "mash", "project-a", original);
        Assert.Equal(original, Assert.Single(_conversations.LoadMessages("history", "mash")).Text);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        var pages = new (int Offset, string Text, int? Next)[]
        {
            (0, "计划", 2), (2, "\0😀", 4), (4, "修订", null)
        };
        var reconstructed = string.Empty;
        foreach (var expected in pages)
        {
            var page = await Invoke(plugin, "experience.read", Live,
                new { reference.sourceId, reference.sourceVersion, offset = expected.Offset, maxChars = 2 });
            Assert.True(page.Success, page.ErrorCode);
            Assert.Equal(expected.Text, page.Payload.GetProperty("text").GetString());
            var next = page.Payload.GetProperty("nextOffset");
            Assert.Equal(expected.Next, next.ValueKind == JsonValueKind.Null ? (int?)null : next.GetInt32());
            var coverage = page.Payload.GetProperty("coverage");
            Assert.Equal("unicode_scalar", coverage.GetProperty("offsetUnit").GetString());
            Assert.Equal(expected.Offset, coverage.GetProperty("offset").GetInt32());
            Assert.Equal(2, coverage.GetProperty("returnedCharacters").GetInt32());
            Assert.Equal(6, coverage.GetProperty("totalCharacters").GetInt32());
            Assert.False(coverage.GetProperty("fullMessageInPage").GetBoolean());
            Assert.Equal(expected.Next is null, coverage.GetProperty("atMessageEnd").GetBoolean());
            reconstructed += page.Payload.GetProperty("text").GetString();
        }
        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public async Task Text_changes_after_nul_invalidate_previously_returned_source_versions()
    {
        const string original = "计划\0原文";
        Seed("history", "mash", "project-a", original);
        Assert.Equal(original, Assert.Single(_conversations.LoadMessages("history", "mash")).Text);
        var owner = new ConversationSourceReader(_conversations);
        var scope = new ToolScope("history", "mash", "project-a");
        var before = owner.Read(scope, "history-m1", ConversationSourceKind.User);
        await using var plugin = CreatePlugin(host => host == Live);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);

        ReplaceOriginalText("计划\0修订");
        var after = owner.Read(scope, "history-m1", ConversationSourceKind.User);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SOURCE_CHANGED");
        var current = await Reference(plugin);
        Assert.Equal(after.Fingerprint, current.sourceVersion);
        Assert.NotEqual(reference.sourceVersion, current.sourceVersion);
    }

    [Theory]
    [InlineData(false, false, 12000)]
    [InlineData(true, false, 6001)]
    [InlineData(false, true, 12000)]
    [InlineData(true, true, 6002)]
    public async Task Legal_12000_unit_originals_remain_readable_within_existing_page_limits(bool emoji, bool nul, int totalScalars)
    {
        var original = nul
            ? "计划\0" + (emoji ? string.Concat(Enumerable.Repeat("😀", 5998)) + "尾" : new string('界', 11997))
            : "计划" + (emoji ? string.Concat(Enumerable.Repeat("😀", 5999)) : new string('界', 11998));
        Assert.Equal(12000, original.Length);
        Seed("history", "mash", "project-a", original);
        Assert.Equal(original, Assert.Single(_conversations.LoadMessages("history", "mash")).Text);
        var owner = new ConversationSourceReader(_conversations).Read(new("history", "mash", "project-a"),
            "history-m1", ConversationSourceKind.User);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        Assert.Equal(owner.Fingerprint, reference.sourceVersion);
        var offset = 0;
        var reconstructed = string.Empty;
        var pages = 0;
        do
        {
            var page = await Invoke(plugin, "experience.read", Live, new { reference.sourceId, reference.sourceVersion, offset });
            Assert.True(page.Success, page.ErrorCode);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(page.Payload.GetRawText()), 1, 48 * 1024);
            var coverage = page.Payload.GetProperty("coverage");
            Assert.Equal(totalScalars, coverage.GetProperty("totalCharacters").GetInt32());
            Assert.Equal(offset, coverage.GetProperty("offset").GetInt32());
            Assert.InRange(coverage.GetProperty("returnedCharacters").GetInt32(), 1, 6000);
            Assert.False(coverage.GetProperty("conversationComplete").GetBoolean());
            reconstructed += page.Payload.GetProperty("text").GetString();
            Assert.InRange(++pages, 1, 10);
            var next = page.Payload.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.Equal(offset + coverage.GetProperty("returnedCharacters").GetInt32(), next.GetInt32());
            offset = next.GetInt32();
        } while (true);
        Assert.Equal(original, reconstructed);
    }

    [Theory]
    [InlineData("ascii", "start", 12000)]
    [InlineData("ascii", "middle", 12000)]
    [InlineData("ascii", "end", 12000)]
    [InlineData("bmp", "start", 12000)]
    [InlineData("bmp", "middle", 12000)]
    [InlineData("bmp", "end", 12000)]
    [InlineData("emoji", "start", 6002)]
    [InlineData("emoji", "middle", 6002)]
    [InlineData("emoji", "end", 6002)]
    [InlineData("ascii", "multiple", 12000)]
    public async Task Public_append_to_experience_read_preserves_long_nul_originals(string kind, string position, int totalScalars)
    {
        var body = "计划" + (kind == "emoji" ? string.Concat(Enumerable.Repeat("😀", 5998))
            : new string(kind == "bmp" ? '界' : 'x', 11996)) + "尾";
        var original = position == "multiple" ? "计划\0" + new string('x', 11994) + "\0尾\0"
            : body.Insert(position == "start" ? 0 : position == "middle" ? 6000 : body.Length, "\0");
        Assert.Equal(12000, original.Length);
        Seed("history", "mash", "project-a", original);
        var reopened = new SqliteConversationRepository(new RuntimeDatabase(_path, pooling: false));
        Assert.Equal(original, Assert.Single(reopened.LoadMessages("history", "mash")).Text);
        var owner = new ConversationSourceReader(reopened).Read(new("history", "mash", "project-a"),
            "history-m1", ConversationSourceKind.User);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);

        // A short literal query after a NUL must still discover the stored source.
        var search = await Invoke(plugin, "experience.search", Live, new { query = "尾" });
        Assert.True(search.Success, search.ErrorCode);
        var item = Assert.Single(search.Payload.GetProperty("items").EnumerateArray());
        var sourceId = item.GetProperty("sourceId").GetString();
        var sourceVersion = item.GetProperty("sourceVersion").GetString();
        Assert.Equal(owner.Fingerprint, sourceVersion);
        Assert.True(item.GetProperty("isTruncated").GetBoolean());
        Assert.Equal("history-m1", item.GetProperty("messageId").GetString());
        Assert.Equal(Now, item.GetProperty("occurredAtUtc").GetDateTimeOffset());
        var offset = 0;
        var reconstructed = new System.Text.StringBuilder();
        var pages = 0;
        do
        {
            var page = await Invoke(plugin, "experience.read", Live, new { sourceId, sourceVersion, offset });
            Assert.True(page.Success, page.ErrorCode);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(page.Payload.GetRawText()), 1, 48 * 1024);
            Assert.Equal(sourceVersion, page.Payload.GetProperty("sourceVersion").GetString());
            var coverage = page.Payload.GetProperty("coverage");
            Assert.Equal("unicode_scalar", coverage.GetProperty("offsetUnit").GetString());
            Assert.Equal(totalScalars, coverage.GetProperty("totalCharacters").GetInt32());
            Assert.Equal(offset, coverage.GetProperty("offset").GetInt32());
            Assert.False(coverage.GetProperty("conversationComplete").GetBoolean());
            var returned = coverage.GetProperty("returnedCharacters").GetInt32();
            Assert.InRange(returned, 1, 6000);
            reconstructed.Append(page.Payload.GetProperty("text").GetString());
            Assert.InRange(++pages, 1, 10);
            var next = page.Payload.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(totalScalars, offset + returned);
                Assert.True(coverage.GetProperty("atMessageEnd").GetBoolean());
                break;
            }
            Assert.False(coverage.GetProperty("atMessageEnd").GetBoolean());
            Assert.Equal(offset + returned, next.GetInt32());
            offset = next.GetInt32();
        } while (true);
        Assert.Equal(original, reconstructed.ToString());
        AssertFailure(await Invoke(plugin, "experience.read", new("live", "mash", "project-b"),
            new { sourceId, sourceVersion }), "EXPERIENCE_SCOPE_DENIED");
    }

    [Theory]
    [InlineData("sentinel")]
    [InlineData("尾部原文")]
    public async Task Public_append_exposes_long_nul_tail_to_experience_search_and_read(string query)
    {
        var original = "计划\0" + new string('x', 1000) + " " + query;
        Seed("history", "mash", "project-a", original);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);

        var search = await Invoke(plugin, "experience.search", Live, new { query });
        Assert.True(search.Success, search.ErrorCode);
        var item = Assert.Single(search.Payload.GetProperty("items").EnumerateArray());
        Assert.Equal("history-m1", item.GetProperty("messageId").GetString());
        var read = await Invoke(plugin, "experience.read", Live, new
        {
            sourceId = item.GetProperty("sourceId").GetString(),
            sourceVersion = item.GetProperty("sourceVersion").GetString()
        });
        Assert.True(read.Success, read.ErrorCode);
        Assert.Equal(original, read.Payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Nul_tail_search_merges_fts_hits_without_duplicates_or_scope_leaks()
    {
        Seed("a-normal", "mash", "project-a", "needle normal");
        Seed("b-both", "mash", "project-a", "needle\0" + new string('x', 1000) + "needle");
        Seed("z-tail", "mash", "project-a", "prefix\0" + new string('x', 1000) + "needle");
        Seed("foreign-project", "mash", "project-b", "prefix\0needle");
        Seed("foreign-role", "other", "project-a", "prefix\0needle");
        Seed("unscoped", "mash", null, "prefix\0needle");
        Append("live", ChatMessageRole.User, "prefix\0needle", ChatMessageStatus.Completed, 2);
        Append("z-tail", ChatMessageRole.Assistant, "prefix\0needle", ChatMessageStatus.Cancelled, 2);
        Append("z-tail", ChatMessageRole.Assistant, "prefix\0needle", ChatMessageStatus.Failed, 3);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);

        var search = await Invoke(plugin, "experience.search", Live, new { query = "needle" });
        Assert.True(search.Success, search.ErrorCode);
        var items = search.Payload.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "a-normal-m1", "b-both-m1", "z-tail-m1" },
            items.Select(item => item.GetProperty("messageId").GetString()).OrderBy(id => id).ToArray());
        Assert.True(search.Payload.GetProperty("coverage").GetProperty("nonExhaustive").GetBoolean());
    }

    [Theory]
    [InlineData(9, 0, true)]
    [InlineData(10, 0, false)]
    [InlineData(0, 199, true)]
    [InlineData(0, 200, false)]
    public async Task Nul_tail_search_respects_the_existing_recent_window(int conversationPadding, int messagePadding, bool found)
    {
        Seed("a-target", "mash", "project-a", "prefix\0needle");
        for (var i = 0; i < conversationPadding; i++) Seed($"z-{i:00}", "mash", "project-a", "padding");
        for (var i = 0; i < messagePadding; i++) Append("a-target", ChatMessageRole.User, "padding", ChatMessageStatus.Completed, i + 2);
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);

        var search = await Invoke(plugin, "experience.search", Live, new { query = "needle" });
        Assert.True(search.Success, search.ErrorCode);
        var items = search.Payload.GetProperty("items").EnumerateArray().ToArray();
        if (found) Assert.Equal("a-target-m1", Assert.Single(items).GetProperty("messageId").GetString());
        else Assert.Empty(items);
        Assert.True(search.Payload.GetProperty("coverage").GetProperty("nonExhaustive").GetBoolean());
    }

    [Theory]
    [InlineData(12001)]
    [InlineData(40000)]
    public async Task Oversized_originals_with_nul_fail_explicitly_instead_of_returning_a_prefix(int units)
    {
        Seed("history", "mash", "project-a", "计划原文");
        await using var plugin = CreatePlugin(scope => scope == Live);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        // A synthetic malformed row can pass SQLite's text length check because it stops at NUL.
        ReplaceOriginalText("计划\0" + new string('界', units - 3));
        var read = new SqliteExperienceSourceReader(_database).Read(new("mash", "project-a"),
            new("history", "history-m1", 1, Now));
        Assert.Equal(ExperienceSourceStatus.Changed, read.Status);
        Assert.Null(read.Source);
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SOURCE_CHANGED");
        var search = await Invoke(plugin, "experience.search", Live, new { query = "计划" });
        Assert.True(search.Success, search.ErrorCode);
        Assert.Equal("partial", search.Payload.GetProperty("status").GetString());
        Assert.Equal(1, search.Payload.GetProperty("omittedCandidates").GetInt32());
        Assert.Empty(search.Payload.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Message_deletion_and_completed_status_changes_invalidate_read_references()
    {
        Seed("history", "mash", "project-a", "计划原文");
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        Execute("UPDATE chat_messages SET status='failed' WHERE message_id='history-m1'");
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SOURCE_CHANGED");
        Execute("DELETE FROM chat_messages WHERE message_id='history-m1'");
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SOURCE_DELETED");
    }

    [Fact]
    public async Task Reference_expiry_and_plugin_shutdown_do_not_return_source_text()
    {
        Seed("history", "mash", "project-a", "计划原文");
        var clock = new TestClock();
        await using var plugin = CreatePlugin(_ => true, clock);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        clock.Now = Now.AddMinutes(6);
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_SOURCE_EXPIRED");
        await plugin.StopAsync(default);
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_STOPPED");
    }

    [Fact]
    public async Task Diagnostics_are_correlated_stages_without_source_content_or_scope()
    {
        Seed("history", "mash", "project-a", "计划原文");
        var diagnostics = new List<ExperienceDiagnostic>();
        await using var plugin = CreatePlugin(_ => true, diagnostics: diagnostics.Add);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        Assert.True((await Invoke(plugin, "experience.read", Live, reference)).Success);
        var logged = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("计划", logged);
        Assert.DoesNotContain("project-a", logged);
        Assert.DoesNotContain("history-m1", logged);
        Assert.DoesNotContain(reference.sourceId, logged);
        Assert.Contains(diagnostics, item => item.Stage == "source_read");
        Assert.Contains(diagnostics, item => item.Stage == "completed");
        Assert.All(diagnostics, item => Assert.Matches("^[A-F0-9]{16}$", item.Correlation));
    }

    [Fact]
    public async Task Synchronous_host_disposal_stops_tools_and_is_idempotent()
    {
        Seed("history", "mash", "project-a", "计划原文");
        var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        var disposable = Assert.IsAssignableFrom<IDisposable>(plugin);
        disposable.Dispose();
        disposable.Dispose();
        await plugin.DisposeAsync();
        AssertFailure(await Invoke(plugin, "experience.read", Live, reference), "EXPERIENCE_STOPPED");
    }

    [Fact]
    public async Task Default_read_pages_make_progress_for_a_legal_large_emoji_message_within_the_byte_budget()
    {
        var original = "计划" + string.Concat(Enumerable.Repeat("😀", 5999));
        Seed("history", "mash", "project-a", original);
        await using var plugin = CreatePlugin(_ => true);
        await plugin.StartAsync(default);
        var reference = await Reference(plugin);
        var offset = 0;
        var reconstructed = string.Empty;
        var pages = 0;
        do
        {
            var result = await Invoke(plugin, "experience.read", Live,
                new { reference.sourceId, reference.sourceVersion, offset });
            Assert.True(result.Success, result.ErrorCode);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Payload.GetRawText()), 1, 48 * 1024);
            var coverage = result.Payload.GetProperty("coverage");
            Assert.Equal(offset, coverage.GetProperty("offset").GetInt32());
            Assert.True(coverage.GetProperty("returnedCharacters").GetInt32() > 0);
            Assert.Equal(6001, coverage.GetProperty("totalCharacters").GetInt32());
            reconstructed += result.Payload.GetProperty("text").GetString();
            Assert.InRange(++pages, 1, 10);
            var next = result.Payload.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Assert.Equal(offset + coverage.GetProperty("returnedCharacters").GetInt32(), next.GetInt32());
            offset = next.GetInt32();
        } while (true);
        Assert.Equal(original, reconstructed);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ExperienceToolsTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private IFgoPetPlugin CreatePlugin(Func<ToolScope, bool> guard, TimeProvider? clock = null,
        Action<ExperienceDiagnostic>? diagnostics = null) => new ExperienceToolsPlugin(
            new SqliteConversationRecallRepository(_database), new SqliteExperienceSourceReader(_database), guard, clock, diagnostics);

    private static ValueTask<ToolResult> Invoke(IFgoPetPlugin plugin, string name, ToolScope scope, object arguments) =>
        Assert.Single(plugin.Contributions.Tools.Where(tool => tool.Descriptor.Name == name))
            .InvokeAsync(new(scope, JsonSerializer.SerializeToElement(arguments)), default);

    private static void AssertFailure(ToolResult result, string code)
    {
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(result.Payload.EnumerateObject());
    }

    private sealed record SourceReference(string sourceId, string sourceVersion);

    private static async Task<SourceReference> Reference(IFgoPetPlugin plugin)
    {
        var result = await Invoke(plugin, "experience.search", Live, new { query = "计划" });
        Assert.True(result.Success);
        var item = Assert.Single(result.Payload.GetProperty("items").EnumerateArray());
        return new(item.GetProperty("sourceId").GetString()!, item.GetProperty("sourceVersion").GetString()!);
    }

    private void Execute(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void ReplaceOriginalText(string text)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_messages SET text=CAST($text AS TEXT) WHERE message_id='history-m1'";
        command.Parameters.AddWithValue("$text", System.Text.Encoding.UTF8.GetBytes(text));
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "SELECT CAST(text AS BLOB) FROM chat_messages WHERE message_id='history-m1'";
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(command.ExecuteScalar())));
    }

    private void Append(string id, ChatMessageRole role, string text, ChatMessageStatus status, int sequence)
    {
        var key = new ContentContextKey("mash", "pack", "1", "default", "1", "1");
        _conversations.Append(new(id + "-m" + sequence, id, "mash", role, text, status, Now, key, sequence));
    }

    private void Seed(string id, string servant, string? project, string text)
    {
        var key = new ContentContextKey(servant, "pack", "1", "default", "1", "1");
        _conversations.CreateConversation(id, servant, key, Now, project, project);
        _conversations.Append(new(id + "-m1", id, servant, ChatMessageRole.User, text,
            ChatMessageStatus.Completed, Now, key, 1));
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}
