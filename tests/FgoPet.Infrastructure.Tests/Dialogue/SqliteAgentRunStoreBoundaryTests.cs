using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Windows.Secrets;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SqliteAgentRunStoreBoundaryTests
{
    [Fact]
    public async Task Run_quota_rejects_the_257th_run_without_partial_rows_or_events()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        using var fixture = new SqliteAgentRunStoreTests.Fixture(clock);

        for (var index = 0; index < 256; index++)
        {
            var state = ForRun(fixture, $"quota-{index:D3}");
            Assert.True(await fixture.Store.TryCreateAsync(state, fixture.Events(state, 1), default));
            Assert.Equal(1, await fixture.Store.CloseInterruptedRunsAsync(default));
        }

        var overflow = ForRun(fixture, "quota-overflow");
        var error = await Assert.ThrowsAsync<AgentStateException>(() =>
            fixture.Store.TryCreateAsync(overflow, fixture.Events(overflow, 1), default).AsTask());

        Assert.Equal("RUN_STATE_QUOTA_EXCEEDED", error.Code);
        Assert.Equal(256, fixture.Count("agent_runs"));
        Assert.Equal(512, fixture.Count("agent_run_events"));
        Assert.Equal(0, Count(fixture.Database, "SELECT COUNT(*) FROM agent_runs WHERE completed_at_utc IS NULL"));
        Assert.Equal(AgentRunStatus.Interrupted, (await fixture.Store.LoadAsync("quota-000", default))!.Snapshot.Status);
    }

    [Fact]
    public async Task Retention_prunes_only_terminal_runs_older_than_seven_days_and_cascades_events()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        using var fixture = new SqliteAgentRunStoreTests.Fixture(clock);
        var old = ForRun(fixture, "retention-old");
        Assert.True(await fixture.Store.TryCreateAsync(old, fixture.Events(old, 1), default));
        Assert.Equal(1, await fixture.Store.CloseInterruptedRunsAsync(default));

        clock.Advance(TimeSpan.FromDays(7));
        var boundary = ForRun(fixture, "retention-boundary");
        Assert.True(await fixture.Store.TryCreateAsync(boundary, fixture.Events(boundary, 1), default));
        Assert.NotNull(await fixture.Store.LoadAsync("retention-old", default));
        Assert.Equal(3, fixture.Count("agent_run_events"));
        Assert.Equal(1, await fixture.Store.CloseInterruptedRunsAsync(default));

        clock.Advance(TimeSpan.FromTicks(1));
        var fresh = ForRun(fixture, "retention-fresh");
        Assert.True(await fixture.Store.TryCreateAsync(fresh, fixture.Events(fresh, 1), default));

        Assert.Null(await fixture.Store.LoadAsync("retention-old", default));
        Assert.NotNull(await fixture.Store.LoadAsync("retention-boundary", default));
        Assert.NotNull(await fixture.Store.LoadAsync("retention-fresh", default));
        Assert.Equal(2, fixture.Count("agent_runs"));
        Assert.Equal(3, fixture.Count("agent_run_events"));
    }

    [Fact]
    public async Task Retention_preserves_an_unfinished_run_older_than_seven_days()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        using var fixture = new SqliteAgentRunStoreTests.Fixture(clock);
        AddConversation(fixture, "second", "user-second");
        var oldActive = ForRun(fixture, "active-old");
        Assert.True(await fixture.Store.TryCreateAsync(oldActive, fixture.Events(oldActive, 1), default));

        clock.Advance(TimeSpan.FromDays(8));
        var newRun = ForRun(fixture, "new-conversation", "conversation-second", "user-second");
        Assert.True(await fixture.Store.TryCreateAsync(newRun, fixture.Events(newRun, 1), default));

        Assert.Equal(2, fixture.Count("agent_runs"));
        Assert.Equal(2, fixture.Count("agent_run_events"));
        Assert.Equal(AgentRunStatus.Created, (await fixture.Store.LoadAsync("active-old", default))!.Snapshot.Status);
    }

    [Fact]
    public async Task Concurrent_creates_for_one_conversation_keep_exactly_one_active_run()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var otherStore = new SqliteAgentRunStore(fixture.Database, new WindowsStateProtector());
        var first = ForRun(fixture, "race-first");
        var second = ForRun(fixture, "race-second");
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);

        Task<bool> Attempt(SqliteAgentRunStore store, AgentRunCheckpoint state) => Task.Run(async () =>
        {
            ready.Signal();
            start.Wait();
            return await store.TryCreateAsync(state, fixture.Events(state, 1), default);
        });

        var firstAttempt = Attempt(fixture.Store, first);
        var secondAttempt = Attempt(otherStore, second);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        start.Set();
        var results = await Task.WhenAll(firstAttempt, secondAttempt);

        Assert.Single(results.Where(result => result));
        Assert.Single(results.Where(result => !result));
        Assert.Equal(1, fixture.Count("agent_runs"));
        Assert.Equal(1, fixture.Count("agent_run_events"));
        Assert.Equal(1, Count(fixture.Database, "SELECT COUNT(*) FROM agent_runs WHERE completed_at_utc IS NULL"));
    }

    [Theory]
    [InlineData(AgentWaitKind.Approval)]
    [InlineData(AgentWaitKind.UserInput)]
    public async Task Reopened_waiting_run_is_interrupted_and_clears_pending_authorization(AgentWaitKind kind)
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = ForRun(fixture, "waiting-run");
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        var waiting = WaitingCheckpoint(initial, kind);
        Assert.True(await fixture.Store.TryCommitAsync(waiting, 0, fixture.Events(waiting, 2), default));

        var reopened = new SqliteAgentRunStore(fixture.Database, new WindowsStateProtector());
        Assert.Equal(1, await reopened.CloseInterruptedRunsAsync(default));
        var loaded = (await reopened.LoadAsync("waiting-run", default))!;

        Assert.Equal(AgentRunStatus.Interrupted, loaded.Snapshot.Status);
        Assert.Equal(AgentCallStatus.NotExecuted, loaded.Calls[0].Status);
        Assert.Null(loaded.Waiting);
        Assert.Null(loaded.PendingApproval);
        Assert.Null(loaded.PendingInput);
        Assert.Equal(3, loaded.JournalSequence);
        Assert.Equal(3, fixture.Count("agent_run_events"));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("call")]
    [InlineData("request")]
    [InlineData("revision")]
    [InlineData("expiry")]
    [InlineData("kind")]
    [InlineData("both-pending")]
    public async Task Malformed_wait_binding_is_rejected_without_checkpoint_or_journal_publication(string mismatch)
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = ForRun(fixture, "invalid-wait");
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        var valid = WaitingCheckpoint(initial, AgentWaitKind.Approval);
        var pending = valid.PendingApproval!;
        var binding = pending.Binding;
        var invalidBinding = mismatch switch
        {
            "identity" => binding with { Identity = binding.Identity with { RunId = "different-run" } },
            "call" => binding with { CallId = "different-call" },
            "request" => binding with { RequestId = "different-request" },
            "revision" => binding with { WaitingRevision = binding.WaitingRevision + 1 },
            "expiry" => binding with { ExpiresAt = binding.ExpiresAt.AddSeconds(1) },
            _ => binding,
        };
        var invalid = valid with
        {
            PendingApproval = pending with { Binding = invalidBinding },
            Waiting = mismatch == "kind" ? valid.Waiting! with { Kind = AgentWaitKind.UserInput } : valid.Waiting,
            PendingInput = mismatch == "both-pending" ? CreateUserInput(binding) : valid.PendingInput,
        };

        var error = await Assert.ThrowsAsync<AgentStateException>(() =>
            fixture.Store.TryCommitAsync(invalid, 0, fixture.Events(invalid, 2), default).AsTask());

        Assert.Equal("RUN_STATE_UNREADABLE", error.Code);
        var stored = (await fixture.Store.LoadAsync("invalid-wait", default))!;
        Assert.Equal(0, stored.Snapshot.Revision);
        Assert.Null(stored.Waiting);
        Assert.Null(stored.PendingApproval);
        Assert.Equal(1, fixture.Count("agent_run_events"));
    }

    [Fact]
    public async Task Unreadable_checkpoint_and_its_journal_remain_available_for_recovery()
    {
        using var fixture = new SqliteAgentRunStoreTests.Fixture();
        var initial = ForRun(fixture, "unreadable-run");
        Assert.True(await fixture.Store.TryCreateAsync(initial, fixture.Events(initial, 1), default));
        fixture.Execute("UPDATE agent_runs SET checkpoint=x'01020304' WHERE run_key='" + RunKey("unreadable-run") + "'");

        var error = await Assert.ThrowsAsync<AgentStateException>(() =>
            fixture.Store.LoadAsync("unreadable-run", default).AsTask());

        Assert.Equal("RUN_STATE_UNREADABLE", error.Code);
        Assert.Equal(1, fixture.Count("agent_runs"));
        Assert.Equal(1, fixture.Count("agent_run_events"));
        Assert.Equal("01020304", ReadCheckpointHex(fixture.Database, RunKey("unreadable-run")));
    }

    private static AgentRunCheckpoint ForRun(SqliteAgentRunStoreTests.Fixture fixture, string runId,
        string conversationId = "conversation", string rootMessageId = "user")
    {
        var initial = fixture.Initial();
        return initial with
        {
            Snapshot = initial.Snapshot with
            {
                Identity = initial.Snapshot.Identity with
                {
                    RunId = runId,
                    RootUserMessageId = rootMessageId,
                    Scope = new(conversationId, "role", null),
                },
            },
        };
    }

    private static AgentRunCheckpoint WaitingCheckpoint(AgentRunCheckpoint initial, AgentWaitKind kind)
    {
        const string callId = "waiting-call";
        const string requestId = "waiting-request";
        var revision = initial.Snapshot.Revision + 1;
        var step = 1;
        var expiry = initial.Snapshot.StartedAt.AddMinutes(15);
        var binding = new InteractionBinding(initial.Snapshot.Identity, step, callId, requestId, revision, expiry);
        var status = kind == AgentWaitKind.Approval ? AgentRunStatus.WaitingApproval : AgentRunStatus.WaitingUserInput;
        var checkpoint = initial with
        {
            Snapshot = initial.Snapshot with
            {
                Revision = revision,
                Status = status,
                StepNumber = step,
                ToolCalls = 1,
            },
            Calls = [new(new(callId, "fixture.command", "{}"), AgentCallStatus.Requested, Charged: true)],
            NextCallIndex = 0,
            JournalSequence = initial.JournalSequence + 1,
            Waiting = new(requestId, kind, step, callId, revision, expiry),
            PendingApproval = kind == AgentWaitKind.Approval
                ? new(binding, new("fixture.plugin", "1.0.0", "fixture.command", new string('A', 64), new string('B', 64), null, 1), "{}")
                : null,
            PendingInput = kind == AgentWaitKind.UserInput ? CreateUserInput(binding) : null,
        };
        return checkpoint;
    }

    private static UserInputRequest CreateUserInput(InteractionBinding binding) =>
        new(binding, [new("continue", "Continue?", ["yes", "no"], AllowMultiple: false)]);

    private static void AddConversation(SqliteAgentRunStoreTests.Fixture fixture, string suffix, string messageId) =>
        fixture.Execute($"INSERT INTO conversations(conversation_id,servant_id,created_at_utc,updated_at_utc,status) VALUES('conversation-{suffix}','role','2026-10-06','2026-10-06','active'); " +
            $"INSERT INTO chat_messages(message_id,conversation_id,servant_id,sequence,role,text,status,created_at_utc) VALUES('{messageId}','conversation-{suffix}','role',1,'user','fixture','completed','2026-10-06');");

    private static long Count(RuntimeDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string RunKey(string runId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)));

    private static string ReadCheckpointHex(RuntimeDatabase database, string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hex(checkpoint) FROM agent_runs WHERE run_key=$key";
        command.Parameters.AddWithValue("$key", key);
        return (string)command.ExecuteScalar()!;
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private long _ticks = initialUtc.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
    }
}
