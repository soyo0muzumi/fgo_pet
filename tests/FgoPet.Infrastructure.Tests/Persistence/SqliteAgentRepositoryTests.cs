using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Persistence;

public sealed class SqliteAgentRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-agent-{Guid.NewGuid():N}.db");

    [Fact]
    public void Event_receipt_is_transactional_and_duplicate_delivery_is_idempotent()
    {
        var database = CreateDatabase();
        var todos = new SqliteTodoRepository(database);
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var todo = new TodoItem("todo-1", "Agent task", null, TodoPriority.Normal, null, at, at);
        todos.Save(todo);
        agents.SaveExecution(new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at));

        var started = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-1");
        var completed = new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.TaskCompleted, at.AddMinutes(2), TodoId: "todo-1");

        Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(started));
        Assert.Equal(AgentEventApplyResult.Applied, agents.ApplyEvent(completed));
        Assert.Equal(AgentEventApplyResult.AlreadyApplied, agents.ApplyEvent(completed));
        Assert.Equal(TodoStatus.Completed, Assert.IsType<TodoItem>(todos.Get("todo-1")).Status);
        Assert.Equal(AgentExecutionStatus.Completed, Assert.IsType<AgentExecution>(agents.GetExecution("execution-1")).Status);
    }

    [Fact]
    public void Out_of_order_event_cannot_move_a_terminal_execution_backwards()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        agents.SaveExecution(new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at)
            .MarkCompleted(at.AddMinutes(2)));

        var lateStarted = new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-1");

        Assert.Equal(AgentEventApplyResult.IgnoredStale, agents.ApplyEvent(lateStarted));
        Assert.Equal(AgentExecutionStatus.Completed, Assert.IsType<AgentExecution>(agents.GetExecution("execution-1")).Status);
    }

    [Fact]
    public void Unknown_outcome_and_previous_execution_id_round_trip_through_sqlite()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var unknown = new AgentExecution(
            "execution-2", "todo-1", "codex", "source-1", "task-2", "dispatch-2", at,
            AgentExecutionStatus.DispatchOutcomeUnknown,
            previousExecutionId: "execution-1");

        agents.SaveExecution(unknown);

        var restored = Assert.IsType<AgentExecution>(agents.GetExecution("execution-2"));
        Assert.Equal(AgentExecutionStatus.DispatchOutcomeUnknown, restored.Status);
        Assert.Equal("execution-1", restored.PreviousExecutionId);
        Assert.Equal("task-2", restored.TaskId);
        Assert.Equal("dispatch-2", restored.DispatchRequestId);
    }

    [Fact]
    public void Terminal_listing_is_ordered_by_end_time_then_execution_id_and_limited()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        agents.SaveExecution(Terminal("execution-z", "task-z", at.AddMinutes(2)));
        agents.SaveExecution(Terminal("execution-b", "task-b", at.AddMinutes(1)));
        agents.SaveExecution(Terminal("execution-a", "task-a", at.AddMinutes(1)));
        agents.SaveExecution(new AgentExecution("execution-u", "todo-u", "codex", "source-1", "task-u", "dispatch-u", at)
            .MarkDispatchOutcomeUnknown(at.AddMinutes(3)));

        var result = agents.ListTerminalExecutions(at.AddMinutes(5), 2);

        Assert.Equal(new[] { "execution-a", "execution-b" }, result.Select(execution => execution.Id));
    }

    [Fact]
    public void Event_receipt_lookup_requires_the_exact_identity_and_sequence()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        agents.SaveExecution(Terminal("execution-1", "task-1", at.AddMinutes(1)));
        Assert.False(agents.HasEventReceipt("codex", "source-1", "task-1", 1));

        agents.SaveExecution(new AgentExecution("execution-2", "todo-2", "codex", "source-1", "task-2", "dispatch-2", at));
        agents.ApplyEvent(new AgentEvent("codex", "source-1", "task-2", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-2"));

        Assert.True(agents.HasEventReceipt("codex", "source-1", "task-2", 1));
        Assert.False(agents.HasEventReceipt("codex", "source-1", "task-2", 2));
        Assert.False(agents.HasEventReceipt("codex", "other-source", "task-2", 1));
    }

    [Fact]
    public void Archive_batch_save_replaces_items_and_rolls_back_header_on_item_failure()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var first = Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1));
        var second = Candidate("execution-2", "task-2", "dispatch-2", at.AddMinutes(2));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.Prepared, first, second));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.CommitPending, second));

        var replaced = Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1"));
        Assert.Equal(AgentArchiveBatchState.CommitPending, replaced.State);
        Assert.Single(replaced.Candidates);
        Assert.Equal("execution-2", replaced.Candidates[0].ExecutionId);

        var duplicateExecutionId = Candidate("execution-2", "task-3", "dispatch-3", at.AddMinutes(3));
        Assert.ThrowsAny<Exception>(() => agents.SaveArchiveBatch(
            Batch("batch-failing", AgentArchiveBatchState.Preparing, second, duplicateExecutionId)));
        Assert.Null(agents.GetArchiveBatch("batch-failing"));
    }

    [Fact]
    public void Incomplete_archive_batches_include_recoverable_states_but_not_completed()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var candidate = Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1));
        agents.SaveArchiveBatch(Batch("batch-preparing", AgentArchiveBatchState.Preparing, candidate));
        agents.SaveArchiveBatch(Batch("batch-prepared", AgentArchiveBatchState.Prepared, Candidate("execution-2", "task-2", "dispatch-2", at.AddMinutes(2))));
        agents.SaveArchiveBatch(Batch("batch-commit", AgentArchiveBatchState.CommitPending, Candidate("execution-3", "task-3", "dispatch-3", at.AddMinutes(3))));
        agents.SaveArchiveBatch(Batch("batch-rejected", AgentArchiveBatchState.Rejected,
            new[] { Candidate("execution-4", "task-4", "dispatch-4", at.AddMinutes(4)) }, safeError: "safe"));
        agents.SaveArchiveBatch(Batch("batch-completed", AgentArchiveBatchState.Completed, Candidate("execution-5", "task-5", "dispatch-5", at.AddMinutes(5))));

        var incomplete = agents.ListIncompleteArchiveBatches();

        Assert.Equal(3, incomplete.Count);
        Assert.DoesNotContain(incomplete, batch => batch.State == AgentArchiveBatchState.Completed);
        Assert.DoesNotContain(incomplete, batch => batch.State == AgentArchiveBatchState.Rejected);
    }

    [Fact]
    public void Completing_commit_pending_batch_prunes_covered_rows_and_is_idempotent()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var todo = new TodoItem("todo-1", "Agent task", null, TodoPriority.Normal, null, at, at);
        new SqliteTodoRepository(database).Save(todo);
        agents.SaveExecution(new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at));
        agents.ApplyEvent(new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-1"));
        agents.ApplyEvent(new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.TaskCompleted, at.AddMinutes(2), TodoId: "todo-1"));
        agents.SaveExecution(new AgentExecution("execution-2", "todo-2", "codex", "source-1", "task-2", "dispatch-2", at));

        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.CommitPending,
            Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(2))));
        agents.CompleteArchiveBatch("batch-1", at.AddMinutes(3));

        Assert.Null(agents.GetExecution("execution-1"));
        Assert.False(agents.HasEventReceipt("codex", "source-1", "task-1", 1));
        Assert.True(agents.GetExecution("execution-2") is not null);
        var completed = Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1"));
        Assert.Equal(AgentArchiveBatchState.Completed, completed.State);
        Assert.Single(completed.Candidates);
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT completed_at_utc FROM agent_archive_batches WHERE batch_id='batch-1'";
            Assert.Equal(at.AddMinutes(3), DateTimeOffset.Parse((string)command.ExecuteScalar()!));
        }

        agents.CompleteArchiveBatch("batch-1", at.AddMinutes(4));

        Assert.Equal(AgentArchiveBatchState.Completed, Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1")).State);
        Assert.True(agents.GetExecution("execution-2") is not null);
    }

    [Fact]
    public void Completing_a_non_commit_pending_batch_does_not_prune_rows()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        agents.SaveExecution(Terminal("execution-1", "task-1", at.AddMinutes(1)));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.Prepared,
            Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1))));

        Assert.Throws<InvalidOperationException>(() => agents.CompleteArchiveBatch("batch-1", at.AddMinutes(2)));
        Assert.NotNull(agents.GetExecution("execution-1"));
        Assert.Equal(AgentArchiveBatchState.Prepared, Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1")).State);
    }

    [Fact]
    public void Completing_a_batch_with_receipts_after_final_sequence_reports_inconsistent_state_and_preserves_rows()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        agents.SaveExecution(new AgentExecution("execution-1", "todo-1", "codex", "source-1", "task-1", "dispatch-1", at));
        agents.ApplyEvent(new AgentEvent("codex", "source-1", "task-1", 1, AgentEventType.TaskStarted, at.AddMinutes(1), TodoId: "todo-1"));
        agents.ApplyEvent(new AgentEvent("codex", "source-1", "task-1", 2, AgentEventType.TaskUpdated, at.AddMinutes(2), TodoId: "todo-1"));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.CommitPending,
            Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1), finalSequence: 1)));

        Assert.Throws<InvalidOperationException>(() => agents.CompleteArchiveBatch("batch-1", at.AddMinutes(3)));

        Assert.True(agents.HasEventReceipt("codex", "source-1", "task-1", 1));
        Assert.True(agents.HasEventReceipt("codex", "source-1", "task-1", 2));
        Assert.NotNull(agents.GetExecution("execution-1"));
        Assert.Equal(AgentArchiveBatchState.CommitPending, Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1")).State);
    }

    [Fact]
    public void Identical_completed_batch_save_preserves_completion_timestamp_and_items()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var candidate = Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.CommitPending, candidate));
        agents.CompleteArchiveBatch("batch-1", at.AddMinutes(3));
        var before = ReadArchiveSnapshot(database, "batch-1");

        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.Completed, candidate));

        var after = ReadArchiveSnapshot(database, "batch-1");
        Assert.Equal(before, after);
        Assert.Equal(candidate, Assert.Single(Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1")).Candidates));
    }

    [Fact]
    public void Completed_batch_rejects_nonterminal_or_changed_content_without_mutation()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var candidate = Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.CommitPending, candidate));
        agents.CompleteArchiveBatch("batch-1", at.AddMinutes(3));
        var before = ReadArchiveSnapshot(database, "batch-1");

        Assert.Throws<InvalidOperationException>(() => agents.SaveArchiveBatch(
            Batch("batch-1", AgentArchiveBatchState.Prepared, candidate)));
        Assert.Throws<InvalidOperationException>(() => agents.SaveArchiveBatch(
            Batch("batch-1", AgentArchiveBatchState.Completed,
                Candidate("execution-2", "task-2", "dispatch-2", at.AddMinutes(2)))));

        Assert.Equal(before, ReadArchiveSnapshot(database, "batch-1"));
    }

    [Fact]
    public void Identical_rejected_batch_save_is_idempotent()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var batch = Batch("batch-1", AgentArchiveBatchState.Rejected,
            new[] { Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1)) }, "safe");
        agents.SaveArchiveBatch(batch);
        var before = ReadArchiveSnapshot(database, "batch-1");

        agents.SaveArchiveBatch(batch);

        Assert.Equal(before, ReadArchiveSnapshot(database, "batch-1"));
        Assert.Equal("safe", Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1")).SafeError);
    }

    [Fact]
    public void Rejected_batch_rejects_other_state_or_changed_content_without_mutation()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var candidate = Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.Rejected, new[] { candidate }, "safe"));
        var before = ReadArchiveSnapshot(database, "batch-1");

        Assert.Throws<InvalidOperationException>(() => agents.SaveArchiveBatch(
            Batch("batch-1", AgentArchiveBatchState.Prepared, new[] { candidate }, "safe")));
        Assert.Throws<InvalidOperationException>(() => agents.SaveArchiveBatch(
            Batch("batch-1", AgentArchiveBatchState.Rejected,
                new[] { candidate }, "changed")));

        Assert.Equal(before, ReadArchiveSnapshot(database, "batch-1"));
    }

    [Fact]
    public void Terminal_batch_rejection_is_transactional_and_keeps_all_items()
    {
        var database = CreateDatabase();
        var agents = new SqliteAgentRepository(database);
        var at = DateTimeOffset.Parse("2026-08-30T08:00:00Z");
        var candidate = Candidate("execution-1", "task-1", "dispatch-1", at.AddMinutes(1));
        agents.SaveArchiveBatch(Batch("batch-1", AgentArchiveBatchState.CommitPending, candidate));
        agents.CompleteArchiveBatch("batch-1", at.AddMinutes(3));
        var before = ReadArchiveSnapshot(database, "batch-1");

        Assert.Throws<InvalidOperationException>(() => agents.SaveArchiveBatch(
            Batch("batch-1", AgentArchiveBatchState.Completed,
                Candidate("execution-2", "task-2", "dispatch-2", at.AddMinutes(2)))));

        Assert.Equal(before, ReadArchiveSnapshot(database, "batch-1"));
        Assert.Equal("execution-1", Assert.Single(Assert.IsType<AgentArchiveBatch>(agents.GetArchiveBatch("batch-1")).Candidates).ExecutionId);
    }

    private static AgentExecution Terminal(string executionId, string taskId, DateTimeOffset endedAt) =>
        new(executionId, $"todo-{taskId}", "codex", "source-1", taskId, $"dispatch-{taskId}", endedAt,
            AgentExecutionStatus.Completed, endedAt, endedAt);

    private static AgentArchiveCandidate Candidate(string executionId, string taskId, string dispatchRequestId, DateTimeOffset endedAt, long finalSequence = 2) =>
        new(executionId, new AgentArchiveIdentity("codex", "source-1", taskId, dispatchRequestId, finalSequence, AgentExecutionStatus.Completed), endedAt,
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    private static AgentArchiveBatch Batch(string batchId, AgentArchiveBatchState state, params AgentArchiveCandidate[] candidates) =>
        Batch(batchId, state, candidates, null);

    private static AgentArchiveBatch Batch(string batchId, AgentArchiveBatchState state, IReadOnlyList<AgentArchiveCandidate> candidates, string? safeError) =>
        new(batchId, DateTimeOffset.Parse("2026-08-30T08:00:00Z"), state, candidates,
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", safeError);

    private static ArchiveSnapshot ReadArchiveSnapshot(RuntimeDatabase database, string batchId)
    {
        using var connection = database.Open();
        using var header = connection.CreateCommand();
        header.CommandText = "SELECT state, completed_at_utc FROM agent_archive_batches WHERE batch_id=$batch";
        header.Parameters.AddWithValue("$batch", batchId);
        using var reader = header.ExecuteReader();
        Assert.True(reader.Read());
        var state = reader.GetString(0);
        var completedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
        reader.Close();

        using var items = connection.CreateCommand();
        items.CommandText = "SELECT COUNT(*), COALESCE(MIN(execution_id), '') FROM agent_archive_items WHERE batch_id=$batch";
        items.Parameters.AddWithValue("$batch", batchId);
        using var itemReader = items.ExecuteReader();
        Assert.True(itemReader.Read());
        return new ArchiveSnapshot(state, completedAt, itemReader.GetInt32(0), itemReader.GetString(1));
    }

    private sealed record ArchiveSnapshot(string State, string? CompletedAt, int ItemCount, string FirstExecutionId);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private RuntimeDatabase CreateDatabase()
    {
        var database = new RuntimeDatabase(_path);
        new RuntimeDatabaseMigrator(database).Migrate();
        return database;
    }
}
