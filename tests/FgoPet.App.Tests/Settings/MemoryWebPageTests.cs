using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FgoPet.App.Memory;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class MemoryWebPageTests : IDisposable
{
    private const string PageId = "ConversationMemory";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-memory-web-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly MemoryCandidateService _memories;

    public MemoryWebPageTests()
    {
        _database = TestRuntimeDatabase.Create(_path);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new SqliteConversationRepository(_database);
        _memories = new MemoryCandidateService(new SqliteMemoryRepository(_database), TimeProvider.System);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            try { File.Delete(_path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Get_returns_snapshot_and_reports_that_no_role_is_selected()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var snapshot = await ReadAsync(page, "memory.get");

        Assert.False(snapshot.GetProperty("hasActiveServant").GetBoolean());
        Assert.Equal("请先选择角色。", snapshot.GetProperty("statusText").GetString());
        Assert.Empty(snapshot.GetProperty("candidates").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("storedMemories").EnumerateArray());
        Assert.Equal(string.Empty, snapshot.GetProperty("selectedCandidateId").GetString());
        Assert.Equal(string.Empty, snapshot.GetProperty("selectedMemoryId").GetString());
    }

    [Fact]
    public async Task Set_enabled_is_immediate_and_reflected_in_the_snapshot()
    {
        var model = new MemoryViewModel(_memories);
        var page = new MemoryWebPage(model);
        // No settings store is supplied, so the view model defaults to enabled.
        Assert.True(model.MemoryEnabled);

        var enabled = await ReadAsync(page, "memory.setEnabled", WithPage("\"value\":true"));

        Assert.True(enabled.GetProperty("memoryEnabled").GetBoolean());
        Assert.True(model.MemoryEnabled);

        var disabled = await ReadAsync(page, "memory.setEnabled", WithPage("\"value\":false"));

        Assert.False(disabled.GetProperty("memoryEnabled").GetBoolean());
        Assert.False(model.MemoryEnabled);
    }

    [Fact]
    public async Task Set_enabled_rejects_unknown_property_and_wrong_value_type()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var extra = await SendAsync(page, "memory.setEnabled", WithPage("\"value\":true,\"unexpected\":1"));
        Assert.False(extra.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", extra.ErrorCode);

        var wrongType = await SendAsync(page, "memory.setEnabled", WithPage("\"value\":\"yes\""));
        Assert.False(wrongType.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", wrongType.ErrorCode);

        var missing = await SendAsync(page, "memory.setEnabled", $"{{\"pageId\":\"{PageId}\"}}");
        Assert.False(missing.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", missing.ErrorCode);
    }

    [Fact]
    public async Task Page_id_missing_or_mismatched_is_rejected()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var missing = await SendAsync(page, "memory.get", "{}");
        Assert.False(missing.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", missing.ErrorCode);

        var mismatched = await SendAsync(page, "memory.get", "{\"pageId\":\"Privacy\"}");
        Assert.False(mismatched.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", mismatched.ErrorCode);
    }

    [Fact]
    public async Task Unknown_command_returns_unknown_command()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var result = await SendAsync(page, "memory.nope", $"{{\"pageId\":\"{PageId}\"}}");

        Assert.False(result.Success);
        Assert.Equal("SETTINGS_UNKNOWN_COMMAND", result.ErrorCode);
    }

    [Fact]
    public async Task Selecting_an_unknown_candidate_or_memory_is_rejected()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var candidate = await SendAsync(page, "memory.selectCandidate",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"missing\"}}");
        Assert.False(candidate.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", candidate.ErrorCode);

        var memory = await SendAsync(page, "memory.selectMemory",
            $"{{\"pageId\":\"{PageId}\",\"memoryId\":\"missing\"}}");
        Assert.False(memory.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", memory.ErrorCode);
    }

    [Fact]
    public async Task Approving_a_candidate_removes_it_from_the_pending_list()
    {
        AddCandidate("a", "candidate-a");
        var model = new MemoryViewModel(_memories) { ActiveServantId = "a" };
        await model.RefreshAsync();
        var page = new MemoryWebPage(model);

        var before = await ReadAsync(page, "memory.get");
        Assert.Contains(before.GetProperty("candidates").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "candidate-a");

        var approved = await ReadAsync(page, "memory.approveCandidate",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-a\"}}");

        Assert.DoesNotContain(approved.GetProperty("candidates").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "candidate-a");
        Assert.NotEmpty(approved.GetProperty("storedMemories").EnumerateArray());
    }

    [Fact]
    public async Task Deleting_all_reports_the_view_model_status_when_the_service_is_missing()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var snapshot = await ReadAsync(page, "memory.deleteAll");

        Assert.Equal("删除服务不可用。", snapshot.GetProperty("statusText").GetString());
    }

    [Fact]
    public async Task Sessions_share_the_singleton_view_model()
    {
        var model = new MemoryViewModel(_memories);
        var first = new MemoryWebPage(model);
        var second = first.CreateSession();

        await SendAsync(first, "memory.setEnabled", WithPage("\"value\":true"));

        var snapshot = await ReadAsync(second, "memory.get");
        Assert.True(snapshot.GetProperty("memoryEnabled").GetBoolean());
    }

    [Fact]
    public async Task Cancelled_token_is_not_swallowed_into_a_failure()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var payload = JsonDocument.Parse($"{{\"pageId\":\"{PageId}\"}}").RootElement.Clone();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => page.HandleCommandAsync(new WebSurfaceMessage("memory.get", null, payload), cancellation.Token)
                .AsTask());
    }

    [Fact]
    public async Task Refresh_reports_counts_for_the_active_role()
    {
        AddCandidate("a", "candidate-a");
        var page = new MemoryWebPage(new MemoryViewModel(_memories) { ActiveServantId = "a" });

        var snapshot = await ReadAsync(page, "memory.refresh");

        Assert.Equal("候选 1 条", snapshot.GetProperty("candidatesStatusText").GetString());
        Assert.Equal("当前角色暂无已确认记忆。", snapshot.GetProperty("storedMemoriesStatusText").GetString());
    }

    [Fact]
    public async Task Editing_a_candidate_updates_its_text_in_the_snapshot()
    {
        AddCandidate("a", "candidate-a");
        var model = new MemoryViewModel(_memories) { ActiveServantId = "a" };
        await model.RefreshAsync();
        var page = new MemoryWebPage(model);

        var edited = await ReadAsync(page, "memory.editCandidate",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-a\",\"text\":\"改后的候选正文\"}}");

        Assert.Contains(edited.GetProperty("candidates").EnumerateArray(),
            item => item.GetProperty("text").GetString() == "改后的候选正文");
    }

    [Fact]
    public async Task Editing_a_stored_memory_updates_its_text_in_the_snapshot()
    {
        var (page, memoryId) = await ApproveOneCandidateAsync();

        var edited = await ReadAsync(page, "memory.editMemory",
            $"{{\"pageId\":\"{PageId}\",\"memoryId\":\"{memoryId}\",\"text\":\"改后的已确认记忆\"}}");

        Assert.Contains(edited.GetProperty("storedMemories").EnumerateArray(),
            item => item.GetProperty("text").GetString() == "改后的已确认记忆");
    }

    [Fact]
    public async Task Disabling_and_deleting_a_stored_memory_are_reflected_in_the_snapshot()
    {
        var (page, memoryId) = await ApproveOneCandidateAsync();

        var disabled = await ReadAsync(page, "memory.disableMemory",
            $"{{\"pageId\":\"{PageId}\",\"memoryId\":\"{memoryId}\"}}");
        Assert.False(disabled.GetProperty("storedMemories").EnumerateArray()
            .Single().GetProperty("isEnabled").GetBoolean());

        var deleted = await ReadAsync(page, "memory.deleteMemory",
            $"{{\"pageId\":\"{PageId}\",\"memoryId\":\"{memoryId}\"}}");
        Assert.Empty(deleted.GetProperty("storedMemories").EnumerateArray());
    }

    [Fact]
    public async Task Select_replacement_requires_both_a_candidate_and_a_memory()
    {
        AddCandidate("a", "candidate-a");
        var model = new MemoryViewModel(_memories) { ActiveServantId = "a" };
        await model.RefreshAsync();
        var page = new MemoryWebPage(model);

        // 候选存在（选择步骤会成功），但没有任何已确认记忆被选中，必须拒绝。
        var withoutMemory = await SendAsync(page, "memory.selectReplacement",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-a\"}}");

        Assert.False(withoutMemory.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", withoutMemory.ErrorCode);
    }

    [Fact]
    public async Task Select_replacement_marks_the_candidate_as_replacing_the_selected_memory()
    {
        AddCandidates("a", "candidate-keep", "candidate-approve");
        var model = new MemoryViewModel(_memories) { ActiveServantId = "a" };
        await model.RefreshAsync();
        var page = new MemoryWebPage(model);

        var approved = await ReadAsync(page, "memory.approveCandidate",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-approve\"}}");
        var memoryId = approved.GetProperty("storedMemories").EnumerateArray()
            .Single().GetProperty("id").GetString();

        await ReadAsync(page, "memory.selectMemory", $"{{\"pageId\":\"{PageId}\",\"memoryId\":\"{memoryId}\"}}");
        var linked = await ReadAsync(page, "memory.selectReplacement",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-keep\"}}");

        Assert.Contains(linked.GetProperty("candidates").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "candidate-keep"
                && item.GetProperty("hasReplacement").GetBoolean());
    }

    [Fact]
    public async Task Edit_commands_require_the_draft_text()
    {
        var page = new MemoryWebPage(new MemoryViewModel(_memories));

        var noText = await SendAsync(page, "memory.editCandidate",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-a\"}}");

        Assert.False(noText.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", noText.ErrorCode);
    }

    private async Task<(ISettingsWebPage Page, string MemoryId)> ApproveOneCandidateAsync()
    {
        AddCandidate("a", "candidate-a");
        var model = new MemoryViewModel(_memories) { ActiveServantId = "a" };
        await model.RefreshAsync();
        var page = new MemoryWebPage(model);

        var approved = await ReadAsync(page, "memory.approveCandidate",
            $"{{\"pageId\":\"{PageId}\",\"candidateId\":\"candidate-a\"}}");
        var memoryId = approved.GetProperty("storedMemories").EnumerateArray()
            .Single().GetProperty("id").GetString()!;
        return (page, memoryId);
    }

    private void AddConversation(string role, string id) => _conversations.CreateConversation(
        id, role, new ContentContextKey(role, "test", "1.0.0", "casual", "p1", "k1"), DateTimeOffset.UtcNow);

    private void AddCandidate(string role, string id)
    {
        // The repository rejects a candidate whose conversation does not exist (scope/source check).
        AddConversation(role, "conversation-" + role);
        new SqliteMemoryRepository(_database).AddCandidate(
            new MemoryCandidate(id, role, "conversation-" + role, "合成记忆", DateTimeOffset.UtcNow));
    }

    private void AddCandidates(string role, params string[] ids)
    {
        // 同一角色下的多条候选共用一条对话记录（作用域外键），因此对话只建一次。
        AddConversation(role, "conversation-" + role);
        var repository = new SqliteMemoryRepository(_database);
        foreach (var id in ids)
        {
            repository.AddCandidate(
                new MemoryCandidate(id, role, "conversation-" + role, "合成记忆", DateTimeOffset.UtcNow));
        }
    }

    private static string WithPage(string extra) => $"{{\"pageId\":\"{PageId}\",{extra}}}";

    private static async Task<WebSurfaceCommandResult> SendAsync(ISettingsWebPage page, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return await page.HandleCommandAsync(
            new WebSurfaceMessage(command, null, document.RootElement.Clone()), CancellationToken.None);
    }

    private static async Task<JsonElement> ReadAsync(ISettingsWebPage page, string command, string? payload = null)
    {
        var result = await SendAsync(page, command, payload ?? $"{{\"pageId\":\"{PageId}\"}}");
        Assert.True(result.Success, result.ErrorCode);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        return document.RootElement.Clone();
    }
}
