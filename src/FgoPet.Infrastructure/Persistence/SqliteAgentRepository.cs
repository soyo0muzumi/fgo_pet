using System.Globalization;
using System.Text.Json;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Persistence;

public sealed class SqliteAgentRepository : IAgentRepository
{
    private readonly RuntimeDatabase _database;

    public SqliteAgentRepository(RuntimeDatabase database) => _database = database;

    public void SaveExecution(AgentExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        EnsureNoOtherActiveExecution(connection, transaction, execution);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_executions(
              execution_id, todo_id, source_type, source_instance, task_id, dispatch_request_id,
              status, started_at_utc, updated_at_utc, ended_at_utc, previous_execution_id)
            VALUES($id, $todo, $source, $instance, $task, $request, $status, $started, $updated, $ended, $previous)
            ON CONFLICT(execution_id) DO UPDATE SET
              todo_id=excluded.todo_id,
              source_type=excluded.source_type,
              source_instance=excluded.source_instance,
              task_id=excluded.task_id,
              dispatch_request_id=excluded.dispatch_request_id,
              status=excluded.status,
              started_at_utc=excluded.started_at_utc,
              updated_at_utc=excluded.updated_at_utc,
              ended_at_utc=excluded.ended_at_utc,
              previous_execution_id=excluded.previous_execution_id
            """;
        AddExecutionParameters(command, execution);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public AgentExecution? GetExecution(string id)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectExecutionSql + " WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExecution(reader) : null;
    }

    public AgentExecution? GetExecution(string sourceType, string sourceInstance, string taskId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectExecutionSql + " WHERE source_type=$source AND source_instance=$instance AND task_id=$task";
        command.Parameters.AddWithValue("$source", sourceType);
        command.Parameters.AddWithValue("$instance", sourceInstance);
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExecution(reader) : null;
    }

    public IReadOnlyList<AgentExecution> ListNonTerminalExecutions()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectExecutionSql + " WHERE status IN ('dispatching','active','attention','dispatch_outcome_unknown') ORDER BY updated_at_utc DESC";
        using var reader = command.ExecuteReader();
        var result = new List<AgentExecution>();
        while (reader.Read()) result.Add(ReadExecution(reader));
        return result;
    }

    public IReadOnlyList<AgentExecution> ListTerminalExecutions(DateTimeOffset endedBefore, int limit)
    {
        if (limit <= 0)
        {
            return Array.Empty<AgentExecution>();
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectExecutionSql + " WHERE status IN ('completed','failed','cancelled') AND ended_at_utc < $endedBefore ORDER BY ended_at_utc ASC, execution_id ASC LIMIT $limit";
        command.Parameters.AddWithValue("$endedBefore", endedBefore.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<AgentExecution>();
        while (reader.Read()) result.Add(ReadExecution(reader));
        return result;
    }

    public bool HasEventReceipt(string sourceType, string sourceInstance, string taskId, long sequence)
    {
        if (sequence < 1)
        {
            return false;
        }

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_event_receipts WHERE source_type=$source AND source_instance=$instance AND task_id=$task AND sequence=$sequence)";
        command.Parameters.AddWithValue("$source", sourceType);
        command.Parameters.AddWithValue("$instance", sourceInstance);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$sequence", sequence);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public void SaveArchiveBatch(AgentArchiveBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using (var header = connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = """
                INSERT INTO agent_archive_batches(batch_id, created_at_utc, state, batch_sha256, safe_error, completed_at_utc)
                VALUES($batch, $created, $state, $sha256, $error, $completed)
                ON CONFLICT(batch_id) DO UPDATE SET
                  created_at_utc=excluded.created_at_utc,
                  state=excluded.state,
                  batch_sha256=excluded.batch_sha256,
                  safe_error=excluded.safe_error,
                  completed_at_utc=excluded.completed_at_utc
                """;
            header.Parameters.AddWithValue("$batch", batch.BatchId);
            header.Parameters.AddWithValue("$created", batch.CreatedAt.ToString("O"));
            header.Parameters.AddWithValue("$state", ToDb(batch.State));
            header.Parameters.AddWithValue("$sha256", batch.BatchSha256);
            header.Parameters.AddWithValue("$error", batch.SafeError ?? (object)DBNull.Value);
            header.Parameters.AddWithValue("$completed", (object)DBNull.Value);
            header.ExecuteNonQuery();
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM agent_archive_items WHERE batch_id=$batch";
            clear.Parameters.AddWithValue("$batch", batch.BatchId);
            clear.ExecuteNonQuery();
        }

        foreach (var candidate in batch.Candidates)
        {
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO agent_archive_items(
                  batch_id, execution_id, source_type, source_instance, task_id, dispatch_request_id,
                  final_sequence, final_status, ended_at_utc, summary_sha256)
                VALUES($batch, $execution, $source, $instance, $task, $request, $sequence, $status, $ended, $summary)
                """;
            item.Parameters.AddWithValue("$batch", batch.BatchId);
            item.Parameters.AddWithValue("$execution", candidate.ExecutionId);
            item.Parameters.AddWithValue("$source", candidate.Identity.SourceType);
            item.Parameters.AddWithValue("$instance", candidate.Identity.SourceInstance);
            item.Parameters.AddWithValue("$task", candidate.Identity.TaskId);
            item.Parameters.AddWithValue("$request", candidate.Identity.DispatchRequestId);
            item.Parameters.AddWithValue("$sequence", candidate.Identity.FinalSequence);
            item.Parameters.AddWithValue("$status", ToDb(candidate.Identity.FinalStatus));
            item.Parameters.AddWithValue("$ended", candidate.EndedAt.ToString("O"));
            item.Parameters.AddWithValue("$summary", candidate.SummarySha256);
            item.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public AgentArchiveBatch? GetArchiveBatch(string batchId)
    {
        using var connection = _database.Open();
        return ReadArchiveBatch(connection, null, batchId);
    }

    public IReadOnlyList<AgentArchiveBatch> ListIncompleteArchiveBatches()
    {
        using var connection = _database.Open();
        var headers = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT batch_id FROM agent_archive_batches WHERE state IN ($preparing, $prepared, $commitPending) ORDER BY created_at_utc ASC, batch_id ASC";
            command.Parameters.AddWithValue("$preparing", ToDb(AgentArchiveBatchState.Preparing));
            command.Parameters.AddWithValue("$prepared", ToDb(AgentArchiveBatchState.Prepared));
            command.Parameters.AddWithValue("$commitPending", ToDb(AgentArchiveBatchState.CommitPending));
            using var reader = command.ExecuteReader();
            while (reader.Read()) headers.Add(reader.GetString(0));
        }

        var result = new List<AgentArchiveBatch>(headers.Count);
        foreach (var batchId in headers)
        {
            var batch = ReadArchiveBatch(connection, null, batchId);
            if (batch is not null) result.Add(batch);
        }

        return result;
    }

    public void CompleteArchiveBatch(string batchId, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        AgentArchiveBatchState state;
        using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.Transaction = transaction;
            stateCommand.CommandText = "SELECT state FROM agent_archive_batches WHERE batch_id=$batch";
            stateCommand.Parameters.AddWithValue("$batch", batchId);
            var value = stateCommand.ExecuteScalar();
            if (value is null || value is DBNull)
            {
                throw new KeyNotFoundException($"Archive batch '{batchId}' was not found.");
            }

            state = FromDbArchiveState((string)value);
        }

        if (state == AgentArchiveBatchState.Completed)
        {
            transaction.Commit();
            return;
        }

        if (state != AgentArchiveBatchState.CommitPending)
        {
            throw new InvalidOperationException($"Archive batch '{batchId}' is not commit-pending.");
        }

        var identities = new List<(string ExecutionId, string SourceType, string SourceInstance, string TaskId, long FinalSequence)>();
        using (var items = connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = "SELECT execution_id, source_type, source_instance, task_id, final_sequence FROM agent_archive_items WHERE batch_id=$batch";
            items.Parameters.AddWithValue("$batch", batchId);
            using var reader = items.ExecuteReader();
            while (reader.Read())
            {
                identities.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
            }
        }

        foreach (var identity in identities)
        {
            using (var higher = connection.CreateCommand())
            {
                higher.Transaction = transaction;
                higher.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_event_receipts WHERE source_type=$source AND source_instance=$instance AND task_id=$task AND sequence>$sequence)";
                higher.Parameters.AddWithValue("$source", identity.SourceType);
                higher.Parameters.AddWithValue("$instance", identity.SourceInstance);
                higher.Parameters.AddWithValue("$task", identity.TaskId);
                higher.Parameters.AddWithValue("$sequence", identity.FinalSequence);
                if (Convert.ToInt64(higher.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException($"Archive batch '{batchId}' has receipts beyond final sequence for {identity.SourceType}/{identity.SourceInstance}/{identity.TaskId}.");
                }
            }

            using (var receipts = connection.CreateCommand())
            {
                receipts.Transaction = transaction;
                receipts.CommandText = "DELETE FROM agent_event_receipts WHERE source_type=$source AND source_instance=$instance AND task_id=$task AND sequence<=$sequence";
                receipts.Parameters.AddWithValue("$source", identity.SourceType);
                receipts.Parameters.AddWithValue("$instance", identity.SourceInstance);
                receipts.Parameters.AddWithValue("$task", identity.TaskId);
                receipts.Parameters.AddWithValue("$sequence", identity.FinalSequence);
                receipts.ExecuteNonQuery();
            }

            using (var execution = connection.CreateCommand())
            {
                execution.Transaction = transaction;
                execution.CommandText = "DELETE FROM agent_executions WHERE execution_id=$execution";
                execution.Parameters.AddWithValue("$execution", identity.ExecutionId);
                execution.ExecuteNonQuery();
            }
        }

        using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = "UPDATE agent_archive_batches SET state=$state, completed_at_utc=$completed WHERE batch_id=$batch";
            complete.Parameters.AddWithValue("$state", ToDb(AgentArchiveBatchState.Completed));
            complete.Parameters.AddWithValue("$completed", completedAt.ToString("O"));
            complete.Parameters.AddWithValue("$batch", batchId);
            complete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public AgentEventApplyResult ApplyEvent(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var execution = ReadExecution(connection, transaction, agentEvent.SourceType, agentEvent.SourceInstance, agentEvent.TaskId);
        if (execution is null)
        {
            throw new KeyNotFoundException($"Agent execution '{agentEvent.Identity}' was not found.");
        }

        if (!string.IsNullOrWhiteSpace(agentEvent.TodoId)
            && !string.Equals(agentEvent.TodoId, execution.TodoId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent event and execution Todo IDs do not match.");
        }

        var priorMaxSequence = ReadMaxSequence(connection, transaction, agentEvent);
        using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT OR IGNORE INTO agent_event_receipts(
                  source_type, source_instance, task_id, sequence, event_type, occurred_at_utc, is_private)
                VALUES($source, $instance, $task, $sequence, $type, $occurred, $private)
                """;
            receipt.Parameters.AddWithValue("$source", agentEvent.SourceType);
            receipt.Parameters.AddWithValue("$instance", agentEvent.SourceInstance);
            receipt.Parameters.AddWithValue("$task", agentEvent.TaskId);
            receipt.Parameters.AddWithValue("$sequence", agentEvent.Sequence);
            receipt.Parameters.AddWithValue("$type", ToDb(agentEvent.EventType));
            receipt.Parameters.AddWithValue("$occurred", agentEvent.OccurredAt.ToString("O"));
            receipt.Parameters.AddWithValue("$private", agentEvent.IsPrivate ? 1 : 0);
            if (receipt.ExecuteNonQuery() == 0)
            {
                transaction.Commit();
                return AgentEventApplyResult.AlreadyApplied;
            }
        }

        if (priorMaxSequence > agentEvent.Sequence || execution.IsTerminal)
        {
            transaction.Commit();
            return AgentEventApplyResult.IgnoredStale;
        }

        if (agentEvent.EventType == AgentEventType.TaskRemoved)
        {
            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM agent_executions WHERE execution_id=$id";
            remove.Parameters.AddWithValue("$id", execution.Id);
            remove.ExecuteNonQuery();
            transaction.Commit();
            return AgentEventApplyResult.Applied;
        }

        var updated = ApplyExecutionEvent(execution, agentEvent);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE agent_executions
                SET status=$status, started_at_utc=$started, updated_at_utc=$updated, ended_at_utc=$ended
                WHERE execution_id=$id
                """;
            update.Parameters.AddWithValue("$status", ToDb(updated.Status));
            update.Parameters.AddWithValue("$started", updated.StartedAt?.ToString("O") ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$updated", updated.UpdatedAt.ToString("O"));
            update.Parameters.AddWithValue("$ended", updated.EndedAt?.ToString("O") ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$id", updated.Id);
            update.ExecuteNonQuery();
        }

        if (agentEvent.EventType is AgentEventType.TaskStarted or AgentEventType.TaskResumed or AgentEventType.AttentionRequired)
        {
            SqliteTodoRepository.UpdateStatus(connection, transaction, execution.TodoId, TodoStatus.Active, agentEvent.OccurredAt);
        }
        else if (agentEvent.EventType == AgentEventType.TaskCompleted)
        {
            SqliteTodoRepository.UpdateStatus(connection, transaction, execution.TodoId, TodoStatus.Completed, agentEvent.OccurredAt);
        }
        else if (agentEvent.EventType is AgentEventType.TaskFailed or AgentEventType.TaskCancelled)
        {
            SqliteTodoRepository.UpdateStatus(connection, transaction, execution.TodoId, TodoStatus.Planned, agentEvent.OccurredAt);
        }

        transaction.Commit();
        return AgentEventApplyResult.Applied;
    }

    public void SaveConnection(PersistedAgentConnection connection, IReadOnlyList<AgentProjectTarget> allowedTargets)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(allowedTargets);
        using var db = _database.Open();
        using var transaction = db.BeginTransaction();
        using (var command = db.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO agent_connections(source_type, display_name, version, enabled, last_event_at_utc, pending_count, capabilities_json)
                VALUES($source, $display, $version, $enabled, $last, $pending, $capabilities)
                ON CONFLICT(source_type) DO UPDATE SET
                  display_name=excluded.display_name, version=excluded.version, enabled=excluded.enabled,
                  last_event_at_utc=excluded.last_event_at_utc, pending_count=excluded.pending_count,
                  capabilities_json=excluded.capabilities_json
                """;
            command.Parameters.AddWithValue("$source", connection.SourceType);
            command.Parameters.AddWithValue("$display", connection.DisplayName);
            command.Parameters.AddWithValue("$version", connection.Version);
            command.Parameters.AddWithValue("$enabled", connection.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$last", connection.LastEventAtUtc?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$pending", connection.PendingCount);
            command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(connection.Capabilities));
            command.ExecuteNonQuery();
        }

        using (var clear = db.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM agent_project_targets WHERE source_type=$source";
            clear.Parameters.AddWithValue("$source", connection.SourceType);
            clear.ExecuteNonQuery();
        }

        foreach (var target in allowedTargets)
        {
            using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO agent_project_targets(source_type, target_id, display_name) VALUES($source, $target, $display)";
            insert.Parameters.AddWithValue("$source", connection.SourceType);
            insert.Parameters.AddWithValue("$target", target.TargetId);
            insert.Parameters.AddWithValue("$display", target.DisplayName);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<PersistedAgentConnection> ListConnections()
    {
        using var db = _database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT source_type, display_name, version, enabled, last_event_at_utc, pending_count, capabilities_json FROM agent_connections ORDER BY source_type";
        using var reader = command.ExecuteReader();
        var result = new List<PersistedAgentConnection>();
        while (reader.Read())
        {
            var capabilities = JsonSerializer.Deserialize<AgentCapabilities>(reader.GetString(6))
                ?? new AgentCapabilities(false, false, OpenMode.None);
            result.Add(new PersistedAgentConnection(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)), reader.GetInt32(5), capabilities));
        }

        return result;
    }

    public IReadOnlyList<AgentProjectTarget> LoadAllowedTargets(string sourceType)
    {
        using var db = _database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT target_id, display_name FROM agent_project_targets WHERE source_type=$source ORDER BY display_name";
        command.Parameters.AddWithValue("$source", sourceType);
        using var reader = command.ExecuteReader();
        var result = new List<AgentProjectTarget>();
        while (reader.Read()) result.Add(new AgentProjectTarget(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static readonly string SelectExecutionSql = "SELECT execution_id, todo_id, source_type, source_instance, task_id, dispatch_request_id, status, started_at_utc, updated_at_utc, ended_at_utc, previous_execution_id FROM agent_executions";

    private static void EnsureNoOtherActiveExecution(SqliteConnection connection, SqliteTransaction transaction, AgentExecution execution)
    {
        if (!execution.IsNonTerminal) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM agent_executions WHERE todo_id=$todo AND status IN ('dispatching','active','attention','dispatch_outcome_unknown') AND execution_id<>$id";
        command.Parameters.AddWithValue("$todo", execution.TodoId);
        command.Parameters.AddWithValue("$id", execution.Id);
        if ((long)command.ExecuteScalar()! > 0)
        {
            throw new InvalidOperationException("A Todo item already has a non-terminal Agent execution.");
        }
    }

    private static void AddExecutionParameters(SqliteCommand command, AgentExecution execution)
    {
        command.Parameters.AddWithValue("$id", execution.Id);
        command.Parameters.AddWithValue("$todo", execution.TodoId);
        command.Parameters.AddWithValue("$source", execution.SourceType);
        command.Parameters.AddWithValue("$instance", execution.SourceInstance);
        command.Parameters.AddWithValue("$task", execution.TaskId);
        command.Parameters.AddWithValue("$request", execution.DispatchRequestId);
        command.Parameters.AddWithValue("$status", ToDb(execution.Status));
        command.Parameters.AddWithValue("$started", execution.StartedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$updated", execution.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$ended", execution.EndedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$previous", execution.PreviousExecutionId ?? (object)DBNull.Value);
    }

    private static AgentExecution? ReadExecution(SqliteConnection connection, SqliteTransaction transaction, string source, string instance, string task)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectExecutionSql + " WHERE source_type=$source AND source_instance=$instance AND task_id=$task";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$instance", instance);
        command.Parameters.AddWithValue("$task", task);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadExecution(reader) : null;
    }

    private static AgentExecution ReadExecution(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        ParseUtc(reader.GetString(8)), FromDb(reader.GetString(6)),
        reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)), reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static long ReadMaxSequence(SqliteConnection connection, SqliteTransaction transaction, AgentEvent agentEvent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM agent_event_receipts WHERE source_type=$source AND source_instance=$instance AND task_id=$task";
        command.Parameters.AddWithValue("$source", agentEvent.SourceType);
        command.Parameters.AddWithValue("$instance", agentEvent.SourceInstance);
        command.Parameters.AddWithValue("$task", agentEvent.TaskId);
        return (long)command.ExecuteScalar()!;
    }

    private static AgentExecution ApplyExecutionEvent(AgentExecution execution, AgentEvent agentEvent) => agentEvent.EventType switch
    {
        AgentEventType.TaskStarted => execution.MarkStarted(agentEvent.OccurredAt),
        AgentEventType.TaskResumed => execution.MarkResumed(agentEvent.OccurredAt),
        AgentEventType.AttentionRequired => execution.MarkAttention(agentEvent.OccurredAt),
        AgentEventType.TaskCompleted => execution.MarkCompleted(agentEvent.OccurredAt),
        AgentEventType.TaskFailed => execution.MarkFailed(agentEvent.OccurredAt),
        AgentEventType.TaskCancelled => execution.MarkCancelled(agentEvent.OccurredAt),
        _ => execution.MarkUpdated(agentEvent.OccurredAt),
    };

    private static AgentArchiveBatch? ReadArchiveBatch(SqliteConnection connection, SqliteTransaction? transaction, string batchId)
    {
        DateTimeOffset createdAt;
        AgentArchiveBatchState state;
        string batchSha256;
        string? safeError;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT created_at_utc, state, batch_sha256, safe_error, completed_at_utc FROM agent_archive_batches WHERE batch_id=$batch";
            command.Parameters.AddWithValue("$batch", batchId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            createdAt = ParseUtc(reader.GetString(0));
            state = FromDbArchiveState(reader.GetString(1));
            batchSha256 = reader.GetString(2);
            safeError = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        var candidates = new List<AgentArchiveCandidate>();
        using (var items = connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = """
                SELECT execution_id, source_type, source_instance, task_id, dispatch_request_id,
                       final_sequence, final_status, ended_at_utc, summary_sha256
                FROM agent_archive_items
                WHERE batch_id=$batch
                ORDER BY rowid
                """;
            items.Parameters.AddWithValue("$batch", batchId);
            using var reader = items.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(new AgentArchiveCandidate(
                    reader.GetString(0),
                    new AgentArchiveIdentity(
                        reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                        reader.GetInt64(5), FromDb(reader.GetString(6))),
                    ParseUtc(reader.GetString(7)), reader.GetString(8)));
            }
        }

        return new AgentArchiveBatch(batchId, createdAt, state, candidates, batchSha256, safeError);
    }

    private static AgentExecutionStatus FromDb(string status) => status switch
    {
        "dispatching" => AgentExecutionStatus.Dispatching,
        "active" => AgentExecutionStatus.Active,
        "attention" => AgentExecutionStatus.Attention,
        "dispatch_outcome_unknown" or "dispatchoutcomeunknown" => AgentExecutionStatus.DispatchOutcomeUnknown,
        "completed" => AgentExecutionStatus.Completed,
        "failed" => AgentExecutionStatus.Failed,
        "cancelled" => AgentExecutionStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown Agent execution status '{status}'."),
    };

    private static string ToDb(AgentExecutionStatus status) => status switch
    {
        AgentExecutionStatus.Dispatching => "dispatching",
        AgentExecutionStatus.Active => "active",
        AgentExecutionStatus.Attention => "attention",
        AgentExecutionStatus.DispatchOutcomeUnknown => "dispatch_outcome_unknown",
        AgentExecutionStatus.Completed => "completed",
        AgentExecutionStatus.Failed => "failed",
        AgentExecutionStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static AgentArchiveBatchState FromDbArchiveState(string state) => state switch
    {
        "preparing" => AgentArchiveBatchState.Preparing,
        "prepared" => AgentArchiveBatchState.Prepared,
        "commit_pending" or "commitpending" => AgentArchiveBatchState.CommitPending,
        "completed" => AgentArchiveBatchState.Completed,
        "rejected" => AgentArchiveBatchState.Rejected,
        _ => throw new InvalidOperationException($"Unknown Agent archive batch state '{state}'."),
    };

    private static string ToDb(AgentArchiveBatchState state) => state switch
    {
        AgentArchiveBatchState.Preparing => "preparing",
        AgentArchiveBatchState.Prepared => "prepared",
        AgentArchiveBatchState.CommitPending => "commit_pending",
        AgentArchiveBatchState.Completed => "completed",
        AgentArchiveBatchState.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string ToDb(AgentEventType type) => type switch
    {
        AgentEventType.TaskDiscovered => "task_discovered",
        AgentEventType.TaskStarted => "task_started",
        AgentEventType.TaskUpdated => "task_updated",
        AgentEventType.AttentionRequired => "attention_required",
        AgentEventType.TaskResumed => "task_resumed",
        AgentEventType.MilestoneReached => "milestone_reached",
        AgentEventType.TaskCompleted => "task_completed",
        AgentEventType.TaskFailed => "task_failed",
        AgentEventType.TaskCancelled => "task_cancelled",
        AgentEventType.TaskRemoved => "task_removed",
        AgentEventType.GoalCompleted => "goal_completed",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
