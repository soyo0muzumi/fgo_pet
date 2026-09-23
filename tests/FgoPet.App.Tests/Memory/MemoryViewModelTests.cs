using System.IO;
using FgoPet.App.Memory;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Memory;

public sealed class MemoryViewModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-memory-view-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly MemoryCandidateService _memories;

    public MemoryViewModelTests()
    {
        _database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new SqliteConversationRepository(_database);
        _memories = new MemoryCandidateService(new SqliteMemoryRepository(_database), TimeProvider.System);
    }

    [Fact]
    public async Task Clearing_the_role_removes_old_selection_and_does_not_report_an_empty_database()
    {
        AddCandidate("a", "candidate-a");
        var model = new MemoryViewModel(_memories);
        model.SetActiveServant("a");
        await model.RefreshAsync();
        model.SelectedCandidate = Assert.Single(model.Candidates);

        model.SetActiveServant(null);
        await model.RefreshAsync();

        Assert.Empty(model.Candidates);
        Assert.Null(model.SelectedCandidate);
        Assert.Contains("选择角色", model.StatusText);
    }

    [Fact]
    public async Task Memory_refresh_no_longer_depends_on_a_conversation_listing_service()
    {
        var model = new MemoryViewModel(_memories);
        model.SetActiveServant("a");

        await model.RefreshAsync();

        Assert.DoesNotContain("会话", model.StatusText);
        Assert.Contains("暂无待审核", model.CandidatesStatusText);
    }

    [Fact]
    public async Task Memory_failure_keeps_its_error_safe_and_does_not_mutate_other_data()
    {
        AddConversation("a", "conversation-a");
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE memory_candidates";
            command.ExecuteNonQuery();
        }
        var model = new MemoryViewModel(_memories) { ActiveServantId = "a" };

        var error = await Record.ExceptionAsync(model.RefreshAsync);

        Assert.Null(error);
        Assert.Equal("conversation-a", Assert.Single(_conversations.ListConversations("a")).ConversationId);
        Assert.Contains("记忆加载失败", model.StatusText);
        Assert.DoesNotContain(_path, model.StatusText);
        Assert.DoesNotContain("memory_candidates", model.StatusText);
    }

    [Fact]
    public async Task Changing_role_clears_selection_and_only_displays_that_roles_memories()
    {
        AddCandidate("a", "candidate-a");
        AddCandidate("b", "candidate-b");
        var model = new MemoryViewModel(_memories);
        model.SetActiveServant("a");
        await model.RefreshAsync();
        model.SelectedCandidate = Assert.Single(model.Candidates);

        model.SetActiveServant("b");
        await model.RefreshAsync();

        Assert.Null(model.SelectedCandidate);
        Assert.Equal("candidate-b", Assert.Single(model.Candidates).CandidateId);
    }

    private void AddConversation(string role, string id) => _conversations.CreateConversation(
        id, role, new ContentContextKey(role, "test", "1.0.0", "casual", "p1", "k1"), DateTimeOffset.UtcNow);

    private void AddCandidate(string role, string id)
    {
        AddConversation(role, "conversation-" + role);
        new SqliteMemoryRepository(_database).AddCandidate(new MemoryCandidate(id, role, "conversation-" + role, "合成记忆", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_late_role_result_cannot_overwrite_the_new_roles_lists_or_status()
    {
        AddConversation("a", "conversation-a");
        AddConversation("b", "conversation-b");
        var pending = new TaskCompletionSource<(IReadOnlyList<MemoryCandidate>, IReadOnlyList<StoredMemory>)>();
        CancellationToken oldToken = default;
        var model = new MemoryViewModel(_memories,
            (role, token) =>
            {
                if (role != "a") return EmptyMemories();
                oldToken = token;
                return pending.Task; // Deliberately ignores cancellation.
            }) { ActiveServantId = "a" };
        var oldRefresh = model.RefreshAsync();
        Assert.True(model.IsLoading);
        Assert.Contains("正在加载", model.CandidatesStatusText);

        model.ActiveServantId = "b";
        await model.RefreshAsync();
        var currentStatus = model.StatusText;
        Assert.True(oldToken.IsCancellationRequested);
        pending.SetResult(([new MemoryCandidate("candidate-a", "a", "conversation-a", "合成测试", DateTimeOffset.UtcNow)], []));
        await oldRefresh;

        Assert.Empty(model.Candidates);
        Assert.Equal("b", model.ActiveServantId);
        Assert.Equal(currentStatus, model.StatusText);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task A_late_failed_refresh_cannot_replace_a_newer_success_for_the_same_role()
    {
        var pending = new TaskCompletionSource<(IReadOnlyList<MemoryCandidate>, IReadOnlyList<StoredMemory>)>();
        var calls = 0;
        var model = new MemoryViewModel(_memories,
            (_, _) => ++calls == 1 ? pending.Task : EmptyMemories()) { ActiveServantId = "a" };
        var oldRefresh = model.RefreshAsync();
        await model.RefreshAsync();
        var currentStatus = model.StatusText;
        pending.SetException(new IOException("synthetic private path"));
        await oldRefresh;

        Assert.Equal(currentStatus, model.StatusText);
        Assert.Contains("暂无待审核", model.CandidatesStatusText);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task Memory_failure_is_safe_and_retry_can_recover_without_restarting()
    {
        var fail = true;
        var model = new MemoryViewModel(_memories, (_, _) =>
            fail ? Task.FromException<(IReadOnlyList<MemoryCandidate>, IReadOnlyList<StoredMemory>)>(new IOException("synthetic private path"))
                : EmptyMemories()) { ActiveServantId = "a" };

        await model.RefreshAsync();
        Assert.Contains("记忆加载失败", model.CandidatesStatusText);
        Assert.DoesNotContain("synthetic", model.StatusText);
        Assert.Contains("记忆加载失败", model.StoredMemoriesStatusText);

        fail = false;
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("暂无待审核", model.CandidatesStatusText);
        Assert.DoesNotContain("失败", model.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clearing_the_role_or_disposing_invalidates_an_inflight_read(bool dispose)
    {
        var pending = new TaskCompletionSource<(IReadOnlyList<MemoryCandidate>, IReadOnlyList<StoredMemory>)>();
        var model = new MemoryViewModel(_memories, (_, _) => pending.Task) { ActiveServantId = "a" };
        var refresh = model.RefreshAsync();
        if (dispose) model.Dispose();
        else model.SetActiveServant(null);
        pending.SetException(new IOException("synthetic private path"));
        await refresh;

        Assert.Empty(model.Candidates);
        Assert.Empty(model.StoredMemories);
        Assert.False(model.IsLoading);
        Assert.DoesNotContain("失败", model.StatusText);
        if (!dispose) Assert.Contains("选择角色", model.CandidatesStatusText);
    }

    private static Task<(IReadOnlyList<MemoryCandidate>, IReadOnlyList<StoredMemory>)> EmptyMemories() =>
        Task.FromResult<(IReadOnlyList<MemoryCandidate>, IReadOnlyList<StoredMemory>)>(([], []));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}
