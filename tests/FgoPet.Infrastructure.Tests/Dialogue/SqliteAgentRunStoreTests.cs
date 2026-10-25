using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Windows.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteAgentRunStoreTests
{
    [Fact]
    public async Task Reopen_preserves_protected_checkpoint_and_atomic_journal()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        var next = initial with { Snapshot = initial.Snapshot with { Revision = 1, Status = AgentRunStatus.Running },
            Calls = [new(new("call", "fixture.read", "{\"private\":\"fixture-private-argument\"}"))],
            JournalSequence = 2 };
        Assert.True(await f.Store.TryCommitAsync(next, 0, f.Events(next, 2), default));
        var reopened = new SqliteAgentRunStore(f.Database, new WindowsStateProtector());
        var loaded = (await reopened.LoadAsync("run", default))!;
        Assert.Equal(next.Calls[0].Call, loaded.Calls[0].Call);
        Assert.Equal(1, loaded.Snapshot.Revision);
        Assert.Equal(8, loaded.Snapshot.Budget.MaxModelRequests);
        Assert.Equal(2, f.Count("agent_run_events"));
        foreach (var file in Directory.GetFiles(f.DirectoryPath))
            Assert.DoesNotContain("fixture-private-argument", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    [Fact]
    public async Task Two_store_instances_same_revision_publish_one_checkpoint_and_event()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        var next = initial with { Snapshot = initial.Snapshot with { Revision = 1, Status = AgentRunStatus.Running }, JournalSequence = 2 };
        var other = new SqliteAgentRunStore(f.Database, new WindowsStateProtector());
        var results = await Task.WhenAll(Task.Run(async () => await f.Store.TryCommitAsync(next, 0, f.Events(next, 2), default)),
            Task.Run(async () => await other.TryCommitAsync(next, 0, f.Events(next, 2), default)));
        Assert.Single(results.Where(value => value));
        Assert.Equal(2, f.Count("agent_run_events"));
    }

    [Fact]
    public async Task Invalid_scope_root_or_sequence_is_rejected_without_partial_create()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        var wrong = initial with { Snapshot = initial.Snapshot with { Identity = initial.Snapshot.Identity with {
            Scope = new("conversation", "other-role", null) } } };
        Assert.False(await f.Store.TryCreateAsync(wrong, f.Events(wrong, 1), default));
        wrong = initial with { Snapshot = initial.Snapshot with { Identity = initial.Snapshot.Identity with { RootUserMessageId = "missing" } } };
        Assert.False(await f.Store.TryCreateAsync(wrong, f.Events(wrong, 1), default));
        Assert.False(await f.Store.TryCreateAsync(initial, f.Events(initial, 2), default));
        Assert.Equal(0, f.Count("agent_runs"));
        Assert.Equal(0, f.Count("agent_run_events"));
    }

    [Fact]
    public async Task Event_failure_rolls_back_checkpoint_update()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        f.Execute("CREATE TRIGGER reject_fixture_event BEFORE INSERT ON agent_run_events WHEN new.sequence=2 BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        var next = initial with { Snapshot = initial.Snapshot with { Revision = 1, Status = AgentRunStatus.Running }, JournalSequence = 2 };
        await Assert.ThrowsAsync<AgentStateException>(() => f.Store.TryCommitAsync(next, 0, f.Events(next, 2), default).AsTask());
        Assert.Equal(0, (await f.Store.LoadAsync("run", default))!.Snapshot.Revision);
        Assert.Equal(1, f.Count("agent_run_events"));
    }

    [Fact]
    public async Task Corrupt_ciphertext_has_no_plaintext_fallback()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        f.Execute("UPDATE agent_runs SET checkpoint=x'01020304'");
        var error = await Assert.ThrowsAsync<AgentStateException>(() => f.Store.LoadAsync("run", default).AsTask());
        Assert.Equal("RUN_STATE_UNREADABLE", error.Code);
    }

    [Theory]
    [InlineData(false, AgentRunStatus.Interrupted)]
    [InlineData(true, AgentRunStatus.ExecutionUnknown)]
    public async Task Startup_closes_unfinished_runs_without_executing_or_approving(bool command, AgentRunStatus status)
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        var next = initial with { Snapshot = initial.Snapshot with { Revision = 1, Status = AgentRunStatus.Running, StepNumber = 1, ToolCalls = 1 },
            Calls = [new(new("call", "fixture.run", "{}"), AgentCallStatus.Started, true,
                command ? ToolEffect.Command : ToolEffect.ReadOnly)], JournalSequence = 2 };
        Assert.True(await f.Store.TryCommitAsync(next, 0, f.Events(next, 2), default));
        var reopened = new SqliteAgentRunStore(f.Database, new WindowsStateProtector());
        Assert.Equal(1, await reopened.CloseInterruptedRunsAsync(default));
        var loaded = (await reopened.LoadAsync("run", default))!;
        Assert.Equal(status, loaded.Snapshot.Status);
        Assert.Equal(command ? AgentCallStatus.ExecutionUnknown : AgentCallStatus.NotExecuted, loaded.Calls[0].Status);
        Assert.Null(loaded.Waiting);
        Assert.Equal(0, await f.Store.CloseInterruptedRunsAsync(default));
    }

    [Fact]
    public async Task Deleting_conversation_cascades_ciphertext_and_journal()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        f.Execute("DELETE FROM conversations WHERE conversation_id='conversation'");
        Assert.Null(await f.Store.LoadAsync("run", default));
        Assert.Equal(0, f.Count("agent_run_events"));
    }

    [Fact]
    public async Task A_second_active_run_is_rejected_and_archived_conversation_can_be_closed()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        var other = initial with { Snapshot = initial.Snapshot with { Identity = initial.Snapshot.Identity with { RunId = "other" } } };
        Assert.False(await f.Store.TryCreateAsync(other, f.Events(other, 1), default));
        f.Execute("UPDATE conversations SET status='archived',project_id='different' WHERE conversation_id='conversation'");
        Assert.Equal(1, await f.Store.CloseInterruptedRunsAsync(default));
        Assert.Equal(AgentRunStatus.Interrupted, (await f.Store.LoadAsync("run", default))!.Snapshot.Status);
    }

    [Fact]
    public async Task Stale_scope_cannot_publish_final_but_recovery_can_close_at_journal_boundary()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        var pending = initial with { Snapshot = initial.Snapshot with { Revision = 1, Status = AgentRunStatus.Running }, JournalSequence = 1023 };
        var events = Enumerable.Range(2, 1022).Select(i => f.Events(pending, i)[0]).ToImmutableArray();
        Assert.True(await f.Store.TryCommitAsync(pending, 0, events, default));
        var exhausted = pending with { Snapshot = pending.Snapshot with { Revision = 2 }, JournalSequence = 1024 };
        Assert.False(await f.Store.TryCommitAsync(exhausted, 1, f.Events(exhausted, 1024), default));
        f.Execute("UPDATE conversations SET project_id='other' WHERE conversation_id='conversation'");
        var final = exhausted with { Snapshot = exhausted.Snapshot with { Status = AgentRunStatus.Completed, CompletedAt = DateTimeOffset.UtcNow },
            FinalText = "Fixture final", FinalDeliveryId = "fixture-delivery" };
        Assert.False(await f.Store.TryCommitAsync(final, 1, f.Events(final, 1024), default));
        Assert.Equal(1, await new SqliteAgentRunStore(f.Database, new WindowsStateProtector()).CloseInterruptedRunsAsync(default));
        Assert.Equal(1024, (await f.Store.LoadAsync("run", default))!.JournalSequence);
    }

    [Fact]
    public async Task Terminal_state_cannot_return_to_running_and_metadata_tamper_fails_closed()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        Assert.True(await f.Store.TryCreateAsync(initial, f.Events(initial, 1), default));
        await f.Store.CloseInterruptedRunsAsync(default);
        var terminal = (await f.Store.LoadAsync("run", default))!;
        var next = terminal with { Snapshot = terminal.Snapshot with { Status = AgentRunStatus.Running, CompletedAt = null, Revision = 2 }, JournalSequence = 3 };
        Assert.False(await f.Store.TryCommitAsync(next, 1, f.Events(next, 3), default));
        f.Execute("UPDATE agent_runs SET role_id='tampered'");
        Assert.Equal("RUN_STATE_UNREADABLE", (await Assert.ThrowsAsync<AgentStateException>(
            () => f.Store.LoadAsync("run", default).AsTask())).Code);
    }

    [Fact]
    public async Task Protected_oversize_and_future_schema_are_rejected_before_publication()
    {
        using var f = new Fixture();
        var initial = f.Initial();
        var future = initial with { SchemaVersion = 2 };
        await Assert.ThrowsAsync<AgentStateException>(() => f.Store.TryCreateAsync(future, f.Events(future, 1), default).AsTask());
        var oversized = initial with { Calls = [new(new("call", "fixture.read", new string('x', 65537)))] };
        await Assert.ThrowsAsync<AgentStateException>(() => f.Store.TryCreateAsync(oversized, f.Events(oversized, 1), default).AsTask());
        Assert.Equal(0, f.Count("agent_runs"));
    }

    internal sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "fgo-native-store-" + Guid.NewGuid().ToString("N"));
        public RuntimeDatabase Database { get; }
        public SqliteAgentRunStore Store { get; }
        public Fixture(TimeProvider? time = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            Database = new(Path.Combine(DirectoryPath, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(Database).Migrate();
            Store = new(Database, new WindowsStateProtector(), time);
            Execute("INSERT INTO conversations(conversation_id,servant_id,created_at_utc,updated_at_utc,status) VALUES('conversation','role','2026-10-06','2026-10-06','active'); INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc) VALUES('user','conversation','role',1,'user','fixture','completed','2026-10-06');");
        }
        public AgentRunCheckpoint Initial() => new() { Snapshot = new() {
            Identity = new("run", "user", new("conversation", "role", null), "route", 1), StartedAt = DateTimeOffset.UtcNow }, JournalSequence = 1 };
        public ImmutableArray<AgentEvent> Events(AgentRunCheckpoint state, long sequence) => [new(sequence, DateTimeOffset.UtcNow,
            sequence == 1 ? AgentEventKind.RunCreated : AgentEventKind.RunStarted,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.Snapshot.Identity.RunId)))[..24], state.Snapshot.StepNumber)];
        public void Execute(string sql) { using var c = Database.Open(); using var q = c.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
        public long Count(string table) { using var c = Database.Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM " + table; return (long)q.ExecuteScalar()!; }
        public void Dispose() { foreach (var file in Directory.GetFiles(DirectoryPath)) File.Delete(file); Directory.Delete(DirectoryPath); }
    }
}
