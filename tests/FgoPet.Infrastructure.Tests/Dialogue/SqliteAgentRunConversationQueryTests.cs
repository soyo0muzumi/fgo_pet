using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Windows.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteAgentRunConversationQueryTests
{
    private static readonly ToolScope Scope = new("conversation", "role", null);

    [Fact]
    public async Task Reopened_store_returns_terminal_runs_for_requested_roots_only()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        fixture.Execute("INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc) VALUES('other-root','conversation','role',2,'user','fixture','completed','2026-10-06');");
        foreach (var status in new[] { AgentRunStatus.Completed, AgentRunStatus.Failed, AgentRunStatus.Cancelled,
            AgentRunStatus.Interrupted, AgentRunStatus.ExecutionUnknown, AgentRunStatus.BudgetExceeded })
            await AddTerminal(fixture, status.ToString(), status);
        await AddTerminal(fixture, "other", AgentRunStatus.Failed, "other-root");
        var active = fixture.Initial();
        Assert.True(await fixture.Store.TryCreateAsync(active, fixture.Events(active, 1), default));

        var reopened = new SqliteAgentRunStore(fixture.Database, new WindowsStateProtector());
        var result = await reopened.LoadConversationRunsAsync(Scope, ["user"], default);

        Assert.Equal(6, result.Count);
        Assert.All(result, run => {
            Assert.True(run.Snapshot.IsTerminal);
            Assert.Equal("user", run.Snapshot.Identity.RootUserMessageId);
            Assert.Equal("fixture-result", run.Calls[0].Result!.Payload.GetProperty("value").GetString());
        });
        Assert.Empty(await reopened.LoadConversationRunsAsync(Scope, ["missing"], default));
        Assert.Empty(await reopened.LoadConversationRunsAsync(Scope, [], default));
    }

    [Theory]
    [InlineData("other-conversation", "role", null)]
    [InlineData("conversation", "other-role", null)]
    [InlineData("conversation", "role", "other-project")]
    public async Task Query_requires_complete_scope_match(string conversation, string role, string? project)
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        await AddTerminal(fixture, "finished", AgentRunStatus.Completed);
        Assert.Empty(await fixture.Store.LoadConversationRunsAsync(new(conversation, role, project), ["user"], default));
    }

    [Fact]
    public async Task Query_requires_current_active_conversation_and_project()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        await AddTerminal(fixture, "old-project", AgentRunStatus.Completed);
        fixture.Execute("UPDATE conversations SET project_id='new-project' WHERE conversation_id='conversation'");
        Assert.Empty(await fixture.Store.LoadConversationRunsAsync(Scope, ["user"], default));
        var projectScope = Scope with { ProjectId = "new-project" };
        Assert.Empty(await fixture.Store.LoadConversationRunsAsync(projectScope, ["user"], default));
        await AddTerminal(fixture, "new-project", AgentRunStatus.Completed, scope: projectScope);
        Assert.Equal("new-project", Assert.Single(await fixture.Store.LoadConversationRunsAsync(projectScope, ["user"], default)).Snapshot.Identity.RunId);
        fixture.Execute("UPDATE conversations SET status='archived' WHERE conversation_id='conversation'");
        Assert.Empty(await fixture.Store.LoadConversationRunsAsync(projectScope, ["user"], default));
    }

    [Fact]
    public async Task Large_root_collection_is_not_truncated()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        await AddTerminal(fixture, "finished", AgentRunStatus.Completed);
        var roots = Enumerable.Range(0, 1024).Select(index => $"missing-{index}").Append("user").Append("user").ToArray();
        Assert.Single(await fixture.Store.LoadConversationRunsAsync(Scope, roots, default));
    }

    [Fact]
    public async Task Query_does_not_decrypt_unrequested_roots_and_rejects_corrupt_requested_state()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        await AddTerminal(fixture, "finished", AgentRunStatus.Completed);
        fixture.Execute("UPDATE agent_runs SET checkpoint=x'01020304'");
        Assert.Empty(await fixture.Store.LoadConversationRunsAsync(Scope, ["missing"], default));
        var error = await Assert.ThrowsAsync<AgentStateException>(() =>
            fixture.Store.LoadConversationRunsAsync(Scope, ["user"], default).AsTask());
        Assert.Equal("RUN_STATE_UNREADABLE", error.Code);
    }

    [Fact]
    public async Task Excess_retained_runs_are_rejected_instead_of_truncated()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        await AddTerminal(fixture, "finished", AgentRunStatus.Completed);
        fixture.Execute("""
            WITH RECURSIVE extra(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM extra WHERE n<256)
            INSERT INTO agent_runs SELECT 'extra-'||n,conversation_id,root_message_id,role_id,project_id,
                revision,journal_sequence,status,completed_at_utc,checkpoint FROM agent_runs,extra;
            """);
        var error = await Assert.ThrowsAsync<AgentStateException>(() =>
            fixture.Store.LoadConversationRunsAsync(Scope, ["user"], default).AsTask());
        Assert.Equal("RUN_STATE_QUOTA_EXCEEDED", error.Code);
    }

    [Fact]
    public async Task Legacy_store_default_is_empty_and_both_implementations_honor_cancellation()
    {
        IAgentRunStore legacy = new LegacyStore();
        Assert.Empty(await legacy.LoadConversationRunsAsync(Scope, ["user"], default));
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            legacy.LoadConversationRunsAsync(Scope, ["user"], cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.LoadConversationRunsAsync(Scope, ["user"], cancellation.Token).AsTask());
    }

    private static async Task AddTerminal(SqliteAgentRunStoreTests.Fixture fixture, string runId,
        AgentRunStatus status, string root = "user", ToolScope? scope = null)
    {
        var initial = fixture.Initial();
        initial = initial with { Snapshot = initial.Snapshot with {
            Identity = initial.Snapshot.Identity with { RunId = runId, RootUserMessageId = root, Scope = scope ?? Scope } } };
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        var terminal = initial with {
            Snapshot = initial.Snapshot with { Revision = 1, Status = status, CompletedAt = DateTimeOffset.UtcNow, StepNumber = 1, ToolCalls = 1 },
            Calls = [new(new("call", "fixture.read", "{}"), AgentCallStatus.Completed, true, ToolEffect.ReadOnly,
                new(true, JsonSerializer.SerializeToElement(new { value = "fixture-result" })))],
            FinalText = status == AgentRunStatus.Completed ? "fixture-final" : null,
            FinalDeliveryId = status == AgentRunStatus.Completed ? "fixture-delivery" : null,
            JournalSequence = 2 };
        Assert.True(await fixture.Store.TryCommitAsync(terminal, 0, fixture.Events(terminal, 2), default));
    }

    private sealed class LegacyStore : IAgentRunStore
    {
        public ValueTask<AgentRunCheckpoint?> LoadAsync(string runId, CancellationToken token) => ValueTask.FromResult<AgentRunCheckpoint?>(null);
        public ValueTask<bool> TryCreateAsync(AgentRunCheckpoint initial, ImmutableArray<AgentEvent> events, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<bool> TryCommitAsync(AgentRunCheckpoint next, long expectedRevision, ImmutableArray<AgentEvent> events,
            CancellationToken token) => ValueTask.FromResult(false);
    }
}
