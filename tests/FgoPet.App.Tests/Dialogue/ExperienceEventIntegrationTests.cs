using System.IO;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ExperienceEventIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly ContentContextKey Key = new("role", "fixture", "1", "default", "1", "1");
    private static readonly ToolScope Host = new("current", "role", "project");
    private const string Initial = "Live2D 的动作使用 blend_ms，先试了 180";
    private const string Changed = "Live2D 的 blend_ms 最终值设为 240";
    private readonly string path = Path.Combine(Path.GetTempPath(), $"fgo-experience-events-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase database;
    private readonly SqliteConversationRepository conversations;
    private readonly SqliteExperienceEventStore events;

    public ExperienceEventIntegrationTests()
    {
        database = TestRuntimeDatabase.Create(path);
        new RuntimeDatabaseMigrator(database).Migrate();
        conversations = new(database);
        events = new(database);
    }

    [Fact]
    public async Task Ending_a_desktop_session_links_experiences_that_are_read_only_on_explicit_search()
    {
        Seed("earlier", Initial);
        Seed("later", Changed);
        Seed(Host.ConversationId, "回顾参数");
        var orchestrator = new ConversationOrchestrator(new NoProvider(), new NoContent(), conversations,
            new PromptComposer(), TimeProvider.System, conversationEnded: (scope, token) =>
                Assert.Equal("linked", events.Consolidate(scope, Now, token)));

        orchestrator.LoadConversation("later", Host.RoleId);
        orchestrator.StartNewConversation(Host.RoleId);
        Assert.Empty(Members());
        orchestrator.LoadConversation("later", Host.RoleId);
        orchestrator.StartNewConversation(Host.RoleId, notifyEnded: true);
        Assert.Equal(2, Members().Length);
        Assert.Equal("unchanged", new SqliteExperienceEventStore(database)
            .Consolidate(new("later", "role", "project"), Now, default));
        Seed("third", "Live2D 的 blend_ms 按这组配置复查");
        orchestrator.LoadConversation("third", Host.RoleId);
        orchestrator.StartNewConversation(Host.RoleId, notifyEnded: true);
        Assert.Single(Members().Select(item => item.EventId).Distinct());
        Assert.Equal(3, Members().Length);

        using var tools = new ExperienceToolsPlugin(new SqliteConversationRecallRepository(database),
            new SqliteExperienceSourceReader(database), scope => scope == Host, TimeProvider.System, null, events);
        await tools.StartAsync(default);
        var search = tools.Contributions.Tools.Single(tool => tool.Descriptor.Name == "experience.search");
        var ordinary = await search.InvokeAsync(new(Host, JsonSerializer.SerializeToElement(new { query = "最终值" })), default);
        Assert.True(ordinary.Success);
        var ungrouped = Assert.Single(ordinary.Payload.GetProperty("items").EnumerateArray());
        Assert.False(ungrouped.TryGetProperty("eventId", out _));

        var expanded = await search.InvokeAsync(new(Host,
            JsonSerializer.SerializeToElement(new { query = "最终值", includeRelated = true })), default);
        Assert.True(expanded.Success);
        var items = expanded.Payload.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.False(items[0].GetProperty("related").GetBoolean());
        Assert.All(items.Skip(1), item => Assert.True(item.GetProperty("related").GetBoolean()));
        Assert.Single(items.Select(item => item.GetProperty("eventId").GetString()).Distinct());

        using var disabled = new ExperienceToolsPlugin(new SqliteConversationRecallRepository(database),
            new SqliteExperienceSourceReader(database), scope => scope == Host);
        await disabled.StartAsync(default);
        var disabledSearch = disabled.Contributions.Tools.Single(tool => tool.Descriptor.Name == "experience.search");
        Assert.Equal("TOOL_INVALID_ARGUMENTS", (await disabledSearch.InvokeAsync(new(Host,
            JsonSerializer.SerializeToElement(new { query = "最终值", includeRelated = true })), default)).ErrorCode);

        var original = items.Single(item => item.GetProperty("messageId").GetString() == "earlier-user");
        var reader = tools.Contributions.Tools.Single(tool => tool.Descriptor.Name == "experience.read");
        var request = new ToolInvocation(Host, JsonSerializer.SerializeToElement(new
        {
            sourceId = original.GetProperty("sourceId").GetString(),
            sourceVersion = original.GetProperty("sourceVersion").GetString()
        }));
        var opened = await reader.InvokeAsync(request, default);
        Assert.True(opened.Success);
        Assert.Equal(Initial, opened.Payload.GetProperty("text").GetString());

        Execute("UPDATE conversations SET status='archived' WHERE conversation_id IN ('later','third')");
        Assert.Equal("EXPERIENCE_SOURCE_CHANGED", (await reader.InvokeAsync(request, default)).ErrorCode);
        Execute("UPDATE conversations SET status='active' WHERE conversation_id IN ('later','third')");
        Execute("DELETE FROM chat_messages WHERE message_id IN ('later-user','third-user')");
        Assert.Empty(Members());
        Assert.Equal("EXPERIENCE_SOURCE_CHANGED", (await reader.InvokeAsync(request, default)).ErrorCode);
        Assert.Single(conversations.LoadMessages("earlier", Host.RoleId));
    }

    [Fact]
    public void Chinese_evidence_can_associate_sessions_while_repeated_small_talk_stays_separate()
    {
        const string topic = "海边灯塔模型的底座与旋转支架的校准";
        Seed("earlier", $"先记录：{topic}，本轮完成测量");
        Seed("later", $"接着处理：{topic}，现在已经完成");
        Seed("opener", "今天好累呀");
        Seed("repeated", "今天好累呀");
        Assert.Equal("no_match", events.Consolidate(new("repeated", "role", "project"), Now, default));
        Assert.Equal("linked", events.Consolidate(new("later", "role", "project"), Now, default));
        Assert.Equal(2, Members().Length);
    }

    [Fact]
    public void Consolidation_abstains_between_unlinked_matches_and_excludes_other_projects()
    {
        Seed("earlier", Initial);
        Seed("other-project", Initial + "，这是另一个项目", "different");
        Seed("later", Changed);
        Seed("competing", "Live2D 的 blend_ms 先测了 100");
        Assert.Equal("ambiguous", events.Consolidate(new("later", "role", "project"), Now, default));
        Assert.Empty(Members());
        conversations.DeleteConversation("competing", "role");
        Assert.Equal("linked", events.Consolidate(new("later", "role", "project"), Now, default));
        Assert.Equal(new[] { "earlier", "later" },
            Members().Select(member => member.ConversationId).Order().ToArray());
    }

    [Theory]
    [InlineData("UPDATE chat_messages SET text='已更正的原文' WHERE message_id='earlier-user'")]
    [InlineData("UPDATE conversations SET project_id='different' WHERE conversation_id='earlier'")]
    [InlineData("DELETE FROM conversations WHERE conversation_id='earlier'")]
    public void Changed_deleted_or_moved_sources_remove_the_association(string mutation)
    {
        Seed("earlier", Initial);
        Seed("later", Changed);
        Assert.Equal("linked", events.Consolidate(new("later", "role", "project"), Now, default));
        Execute(mutation);
        Assert.Empty(Members());
    }

    [Fact]
    public void Export_and_restore_exclude_derived_links()
    {
        Seed("earlier", Initial);
        Seed("later", Changed);
        Assert.Equal("linked", events.Consolidate(new("later", "role", "project"), Now, default));
        Assert.Empty(events.Export().GetProperty("members").EnumerateArray());
        var copyPath = path + ".copy";
        try
        {
            using (var db = database.Open())
            using (var vacuum = db.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", copyPath);
                vacuum.ExecuteNonQuery();
            }
            var copy = TestRuntimeDatabase.Create(copyPath);
            Assert.Equal(2, Members(copy).Length);
            events.NormalizeRestoredCopy(copyPath);
            Assert.Empty(Members(copy));
            Assert.Single(new SqliteConversationRepository(copy).LoadMessages("earlier", "role"));
            Assert.Equal(2, Members().Length);
            events.ForgetConversation("earlier", "role");
            Assert.Empty(Members());
        }
        finally { DeleteFiles(copyPath); }
    }

    private (string EventId, string ConversationId)[] Members(RuntimeDatabase? target = null)
    {
        using var db = (target ?? database).Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name='experience_event_members')";
        if ((long)command.ExecuteScalar()! == 0) return [];
        command.CommandText = """
            SELECT a.event_id,m.conversation_id FROM experience_event_members a
            JOIN chat_messages m ON m.message_id=a.message_id
            """;
        using var rows = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (rows.Read()) result.Add((rows.GetString(0), rows.GetString(1)));
        return result.ToArray();
    }

    private void Seed(string id, string text, string project = "project")
    {
        conversations.CreateConversation(id, "role", Key, Now, project);
        conversations.Append(new(id + "-user", id, "role", ChatMessageRole.User, text,
            ChatMessageStatus.Completed, Now, Key, 1));
    }

    private void Execute(string sql)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => DeleteFiles(path);

    private static void DeleteFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(dbPath + suffix);
    }

    private sealed class NoProvider : IChatProviderResolver
    {
        public IChatProvider Resolve() => throw new InvalidOperationException("No provider may be used.");
    }

    private sealed class NoContent : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken token) =>
            throw new InvalidOperationException("No content resolver may be used.");
    }
}
